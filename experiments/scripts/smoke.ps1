#!/usr/bin/env pwsh
# ⚠️ Outdated — references the superseded E0/E1/E_RPC + http_request generation (removed
# raw-HTTP mcp-http client). The current harness runs via experiments/orchestrator (Smoke.fs
# matrices); web arms interact through browsegrab. Retained for historical context.
<#
.SYNOPSIS
G0 smoke test — runs 4 canary cells through the orchestrator and validates output.
.DESCRIPTION
Prerequisites:
  - LM Studio running at http://127.0.0.1:1234 (or override with -BaseUrl)
  - Model qwen2.5-14b-instruct:2 loaded, context length 32768, API format: Anthropic
  - python / python3 available in PATH
#>

param(
    [string]$BaseUrl = "http://127.0.0.1:1234",
    [string]$ApiKey  = "lm-studio",
    [string]$Model   = "qwen2.5-14b-instruct:2"
)

$PriorBaseUrl = $env:ANTHROPIC_BASE_URL
$PriorApiKey  = $env:ANTHROPIC_API_KEY
$env:ANTHROPIC_BASE_URL = $BaseUrl
$env:ANTHROPIC_API_KEY  = $ApiKey
try {

$RepoRoot            = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$OutputDir           = Join-Path $RepoRoot "experiments" "results" "smoke"
$OrchestratorProject = Join-Path $RepoRoot "experiments" "orchestrator"
$UvAvailable         = [bool](Get-Command uv -ErrorAction SilentlyContinue)
$Python              = if ($UvAvailable) { "uv" } elseif (Get-Command py -ErrorAction SilentlyContinue) { "py" } else { "python3" }
$PythonArgs          = if ($UvAvailable) { @("run","python") } else { @() }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$Cells = @(
    @{ Name = "smoke-proto-e1";  Setup = "HTTP";  ExtraArgs = @("--commit","HEAD","--variant","proto",  "--setup","E1",    "--persona","beginner") }
    @{ Name = "smoke-proto-e0";  Setup = "HTTP";  ExtraArgs = @("--commit","HEAD","--variant","proto",  "--setup","E0",    "--persona","beginner") }
    @{ Name = "smoke-simple-e1"; Setup = "HTTP";  ExtraArgs = @("--commit","HEAD","--variant","simple", "--setup","E1",    "--persona","beginner") }
    @{ Name = "smoke-erpc";      Setup = "E_RPC"; ExtraArgs = @(                                        "--setup","E_RPC", "--persona","beginner") }
)

Write-Host ""
Write-Host "=== G0 Smoke Test ===" -ForegroundColor Cyan
Write-Host "Model:    $Model"
Write-Host "Base URL: $BaseUrl"
Write-Host "Output:   $OutputDir"
Write-Host "Note: HTTP cells run dotnet publish on first run — allow 60-90s per cell."
Write-Host ""

# ── Run cells ──────────────────────────────────────────────────────────────────

$CellResults = @()

$LASTEXITCODE = 0
foreach ($Cell in $Cells) {
    $OutputFile = Join-Path $OutputDir "$($Cell.Name).json"
    $AllArgs = @(
        "run", "--project", $OrchestratorProject, "--",
        "run",
        "--model",       $Model,
        "--games",       "1",
        "--temperature", "0.0",
        "--output",      $OutputFile
    ) + $Cell.ExtraArgs

    Write-Host "─── $($Cell.Name) ───" -ForegroundColor Yellow
    $StartTime = Get-Date
    & dotnet @AllArgs
    $ExitCode = $LASTEXITCODE
    $Elapsed  = [math]::Round(((Get-Date) - $StartTime).TotalSeconds, 1)

    if ($ExitCode -eq 0) {
        Write-Host "  Completed in ${Elapsed}s" -ForegroundColor Green
    } else {
        Write-Host "  FAILED (exit $ExitCode) in ${Elapsed}s" -ForegroundColor Red
    }

    $CellResults += [pscustomobject]@{
        Name     = $Cell.Name
        Setup    = $Cell.Setup
        Output   = $OutputFile
        ExitCode = $ExitCode
        Elapsed  = $Elapsed
    }
}

# ── Python validation ──────────────────────────────────────────────────────────

$PyScript = Join-Path ([System.IO.Path]::GetTempPath()) "smoke-$([guid]::NewGuid()).py"
try {
    Set-Content -Path $PyScript -Encoding UTF8 -Value @'
import json, math, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from urllib.parse import urlparse

def get_paths(path):
    try:
        with open(path) as f:
            d = json.load(f)
        return set(
            urlparse(e['url']).path
            for g in d.get('games', [])
            for e in g.get('transcript', [])
            if 'url' in e
        )
    except Exception:
        return set()

def validate(path, setup):
    issues, stats = [], {}
    try:
        with open(path) as f:
            d = json.load(f)
    except Exception as e:
        return [f"Cannot parse JSON: {e}"], stats

    for key in ('cell', 'games', 'aggregate'):
        if key not in d:
            issues.append(f"Missing top-level key: {key}")

    agg = d.get('aggregate', {})
    rpva = agg.get('rpva')
    stats = {
        'rpva':         rpva,
        'invalid_rate': agg.get('invalid_rate'),
        'abandon_rate': agg.get('abandon_rate'),
    }
    if rpva is None:
        issues.append("aggregate.rpva missing")
    elif not math.isfinite(rpva):
        issues.append(f"aggregate.rpva not finite: {rpva}")

    transcript = [e for g in d.get('games', []) for e in g.get('transcript', [])]
    if not transcript:
        if setup == 'E_RPC':
            issues.append("E_RPC: No transcript entries recorded")
        else:
            stats['warning'] = "Empty transcript: model did not call http_request tool (model-prompt compatibility finding; harness is structurally sound)"
        return issues, stats

    if setup == 'E_RPC':
        http_n = sum(1 for e in transcript if 'status' in e)
        tool_n = sum(1 for e in transcript if 'tool_name' in e)
        if http_n:
            issues.append(f"E_RPC: {http_n} unexpected HTTP entries (expected 0)")
        if not tool_n:
            issues.append("E_RPC: no tool_name entries found")
        stats['tool_entries'] = tool_n
    else:
        missing = [e for e in transcript if 'strategy' not in e]
        if missing:
            issues.append(f"{len(missing)} transcript entries missing 'strategy' field")
        n     = len(transcript)
        blind = sum(1 for e in transcript if e.get('strategy') == 'BlindPost')
        stats['blind_post_rate'] = blind / n if n else 0.0
        stats['total_requests']  = n

    return issues, stats

# args: path setup path setup ...
args  = sys.argv[1:]
cells = [(args[i], args[i + 1]) for i in range(0, len(args), 2)]
all_stats    = {}
all_passed   = True
proto_paths  = None
simple_paths = None

for path, setup in cells:
    label  = path.replace('\\', '/').split('/')[-1].replace('.json', '')
    issues, stats = validate(path, setup)
    ok = not issues
    if not ok:
        all_passed = False
    all_stats[label] = (ok, stats, issues)
    if 'proto-e1' in label:
        proto_paths  = get_paths(path)
    if 'simple-e1' in label:
        simple_paths = get_paths(path)

# Path parity
if proto_paths is not None and simple_paths is not None:
    shared  = proto_paths & simple_paths
    missing = proto_paths - simple_paths
    extra   = simple_paths - proto_paths
    print(f"\nPath parity (V_simple vs V_proto): {len(shared)} shared, {len(missing)} missing from simple, {len(extra)} extra in simple")
    if missing:
        print(f"  Paths in V_proto not seen in V_simple: {sorted(missing)}")
    if extra:
        print(f"  Paths in V_simple not seen in V_proto: {sorted(extra)}")

# Summary table
print("\n=== Validation Results ===")
print(f"{'Cell':<24} {'RPVA':<9} {'Inv%':<7} {'Abn%':<7} {'Result'}")
print("─" * 62)
for label, (ok, stats, issues) in all_stats.items():
    rpva_v = stats.get('rpva')
    rpva = ("n/a" if rpva_v is None
            else ("MaxFloat" if rpva_v > 1e300
                  else f"{rpva_v:.3f}"))
    inv  = f"{stats['invalid_rate']*100:.1f}%" if stats.get('invalid_rate') is not None else "n/a"
    abn  = f"{stats['abandon_rate']*100:.1f}%" if stats.get('abandon_rate') is not None else "n/a"
    print(f"{label:<24} {rpva:<9} {inv:<7} {abn:<7} {'PASS' if ok else 'FAIL'}")
    for issue in issues:
        print(f"  ! {issue}")
    if 'warning' in stats:
        print(f"  ~ WARNING: {stats['warning']}")
    if 'blind_post_rate' in stats:
        n = stats['total_requests']
        b = int(stats['blind_post_rate'] * n)
        print(f"  blind-POST rate: {stats['blind_post_rate']:.1%} ({b}/{n})")
    if 'tool_entries' in stats:
        print(f"  tool entries: {stats['tool_entries']}")

print("\n=== Follow-up Items ===")
print("  - [ ] Add blind_post_rate to Aggregate in Types.fs and Metrics.fs")
has_empty_http = any('warning' in stats for _, (_, stats, _) in all_stats.items())
if has_empty_http:
    print("  - [ ] HTTP cells: Qwen2.5-14B does not call http_request spontaneously with minimal beginner prompt")
    print("        Options: (a) add tool_choice:any flag to orchestrator, (b) strengthen E1 prompt for local models")

sys.exit(0 if all_passed else 1)
'@

    Write-Host ""
    Write-Host "─── Validation ───" -ForegroundColor Yellow
    $env:PYTHONIOENCODING = "utf-8"

    $PyArgs = @()
    foreach ($r in $CellResults) {
        $PyArgs += $r.Output
        $PyArgs += $r.Setup
    }

    & $Python @PythonArgs $PyScript @PyArgs
    $ValidationExitCode = $LASTEXITCODE
} finally {
    Remove-Item $PyScript -ErrorAction SilentlyContinue
}

# ── Final verdict ──────────────────────────────────────────────────────────────

$OrchFailed = @($CellResults | Where-Object { $_.ExitCode -ne 0 }).Count
$AllPassed  = ($OrchFailed -eq 0) -and ($ValidationExitCode -eq 0)

Write-Host ""
if ($AllPassed) {
    Write-Host "=== SMOKE PASSED — pipeline is sound, proceed to F0 ===" -ForegroundColor Green
} else {
    Write-Host "=== SMOKE FAILED — investigate before proceeding to F0 ===" -ForegroundColor Red
    if ($OrchFailed -gt 0) {
        Write-Host "$OrchFailed orchestrator cell(s) exited non-zero:" -ForegroundColor Red
        $CellResults | Where-Object { $_.ExitCode -ne 0 } | ForEach-Object {
            Write-Host "  - $($_.Name) (exit $($_.ExitCode))" -ForegroundColor Red
        }
    }
}

exit ([int](-not $AllPassed))

} finally {
    if ($null -eq $PriorBaseUrl) { Remove-Item Env:ANTHROPIC_BASE_URL -EA SilentlyContinue } else { $env:ANTHROPIC_BASE_URL = $PriorBaseUrl }
    if ($null -eq $PriorApiKey)  { Remove-Item Env:ANTHROPIC_API_KEY  -EA SilentlyContinue } else { $env:ANTHROPIC_API_KEY  = $PriorApiKey  }
}
