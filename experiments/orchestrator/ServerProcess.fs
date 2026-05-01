module TicTacToe.Orchestrator.ServerProcess

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types

// ── Port helpers ──────────────────────────────────────────────────────────────

let private findFreePort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

// ── Build helpers ─────────────────────────────────────────────────────────────

let private runProcess (workDir: string) (exe: string) (args: string) : unit =
    let psi = ProcessStartInfo(exe, args,
        WorkingDirectory = workDir,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        UseShellExecute = false)
    use p = Process.Start(psi)
    p.WaitForExit()
    if p.ExitCode <> 0 then
        failwithf "`%s %s` exited with code %d" exe args p.ExitCode

/// Resolve output directory for a given commit + variant.
/// If commit = "HEAD", builds from the working tree directly.
let private resolveOutputDir (repoRoot: string) (commit: string) (variant: Variant) : string =
    let variantName = Variant.toString variant
    if commit = "HEAD" then
        // Use the current build output
        let projPath = Path.Combine(repoRoot, Variant.projectPath variant)
        let publishDir = Path.Combine(repoRoot, ".claude", "worktrees", $"orch-HEAD-{variantName}", "publish")
        Directory.CreateDirectory(publishDir) |> ignore
        runProcess repoRoot "dotnet" $"publish \"{projPath}\" -o \"{publishDir}\" -c Release --nologo -v q"
        publishDir
    else
        let worktreeDir = Path.Combine(repoRoot, ".claude", "worktrees", $"orch-{commit.[..6]}-{variantName}")
        let publishDir = Path.Combine(worktreeDir, "publish")
        if not (Directory.Exists(publishDir)) then
            // Create worktree if it doesn't exist
            if not (Directory.Exists(worktreeDir)) then
                runProcess repoRoot "git" $"worktree add \"{worktreeDir}\" {commit}"
            let projPath = Path.Combine(worktreeDir, Variant.projectPath variant)
            Directory.CreateDirectory(publishDir) |> ignore
            runProcess worktreeDir "dotnet" $"publish \"{projPath}\" -o \"{publishDir}\" -c Release --nologo -v q"
        publishDir

// ── Server process ────────────────────────────────────────────────────────────

type ServerHandle = {
    Process: Process
    BaseUrl: string
    mutable Disposed: bool
}
    with
        interface IDisposable with
            member this.Dispose() =
                if not this.Disposed then
                    this.Disposed <- true
                    try this.Process.Kill(entireProcessTree = true) with _ -> ()
                    this.Process.Dispose()

/// Start the server and wait until it responds on the health endpoint.
let startServer (repoRoot: string) (commit: string) (variant: Variant) : Task<ServerHandle> =
    task {
        let publishDir = resolveOutputDir repoRoot commit variant
        let exeName =
            match variant with
            | Proto -> "TicTacToe.Web"
            | Simple -> "TicTacToe.Web.Simple"
        let exePath = Path.Combine(publishDir, exeName + (if OperatingSystem.IsWindows() then ".exe" else ""))

        let port = findFreePort()
        let baseUrl = $"http://localhost:{port}"

        let psi = ProcessStartInfo(exePath,
            $"--urls {baseUrl}",
            WorkingDirectory = publishDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false)
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] <- "Production"

        let proc = Process.Start(psi)

        // Wait for server to be ready (poll /login up to 30s)
        use httpClient = new System.Net.Http.HttpClient()
        httpClient.Timeout <- TimeSpan.FromSeconds(2.0)
        let deadline = DateTime.UtcNow.AddSeconds(30.0)
        let mutable ready = false
        while not ready && DateTime.UtcNow < deadline do
            try
                let! resp = httpClient.GetAsync($"{baseUrl}/login")
                if resp.IsSuccessStatusCode || int resp.StatusCode = 302 || int resp.StatusCode = 200 then
                    ready <- true
            with _ -> ()
            if not ready then do! Task.Delay(500)

        if not ready then
            proc.Kill(entireProcessTree = true)
            failwithf "Server did not start within 30s at %s" baseUrl

        return { Process = proc; BaseUrl = baseUrl; Disposed = false }
    }
