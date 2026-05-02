#!/usr/bin/env pwsh
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

$env:ANTHROPIC_BASE_URL = $BaseUrl
$env:ANTHROPIC_API_KEY  = $ApiKey

$RepoRoot            = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$OutputDir           = Join-Path $RepoRoot "experiments" "results" "smoke"
$OrchestratorProject = Join-Path $RepoRoot "experiments" "orchestrator"
$Python              = if (Get-Command python3 -ErrorAction SilentlyContinue) { "python3" } else { "python" }

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

$PyScript = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.py'

Set-Content -Path $PyScript -Encoding UTF8 -Value @'
import json, math, sys
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
        issues.append("No transcript entries recorded")
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
    rpva = f"{stats['rpva']:.3f}"             if isinstance(stats.get('rpva'), float) else "n/a"
    inv  = f"{stats['invalid_rate']*100:.1f}%" if stats.get('invalid_rate') is not None else "n/a"
    abn  = f"{stats['abandon_rate']*100:.1f}%" if stats.get('abandon_rate') is not None else "n/a"
    print(f"{label:<24} {rpva:<9} {inv:<7} {abn:<7} {'PASS' if ok else 'FAIL'}")
    for issue in issues:
        print(f"  ! {issue}")
    if 'blind_post_rate' in stats:
        n = stats['total_requests']
        b = int(stats['blind_post_rate'] * n)
        print(f"  blind-POST rate: {stats['blind_post_rate']:.1%} ({b}/{n})")
    if 'tool_entries' in stats:
        print(f"  tool entries: {stats['tool_entries']}")

print("\n=== Follow-up Items ===")
print("  - [ ] Add blind_post_rate to Aggregate in Types.fs and Metrics.fs")

sys.exit(0 if all_passed else 1)
'@

Write-Host ""
Write-Host "─── Validation ───" -ForegroundColor Yellow

$PyArgs = @()
foreach ($r in $CellResults) {
    $PyArgs += $r.Output
    $PyArgs += $r.Setup
}

& $Python $PyScript @PyArgs
$ValidationExitCode = $LASTEXITCODE
Remove-Item $PyScript -ErrorAction SilentlyContinue

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
