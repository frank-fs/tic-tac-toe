namespace TicTacToe.Web.Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading

/// Launches the TicTacToe.Web app as a child process with a chosen game configuration
/// (InitialGames/MaxGames via the same env vars the orchestrator uses), on a free port.
/// Runs the already-built dll (the test project references TicTacToe.Web, so it is built
/// alongside the tests) to avoid `dotnet run` rebuild/file-lock issues. Disposing kills it.
type ConfiguredServer(initialGames: int, maxGames: int) =

    static let repoRoot () =
        let rec up (dir: DirectoryInfo) =
            if isNull dir then failwith "repo root (TicTacToe.sln) not found"
            elif File.Exists(Path.Combine(dir.FullName, "TicTacToe.sln")) then dir.FullName
            else up dir.Parent
        up (DirectoryInfo(AppContext.BaseDirectory))

    static let freePort () =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        port

    static let startProcess (baseUrl: string) (initialGames: int) (maxGames: int) =
        let baseDir = DirectoryInfo(AppContext.BaseDirectory)   // .../bin/<Config>/<tfm>
        let tfm = baseDir.Name
        let config = baseDir.Parent.Name
        let dll = Path.Combine(repoRoot (), "src", "TicTacToe.Web", "bin", config, tfm, "TicTacToe.Web.dll")
        if not (File.Exists dll) then failwithf "web app dll not found: %s (build TicTacToe.Web first)" dll
        let psi = ProcessStartInfo("dotnet")
        psi.ArgumentList.Add dll
        psi.ArgumentList.Add "--urls"
        psi.ArgumentList.Add baseUrl
        psi.Environment.["TICTACTOE_INITIAL_GAMES"] <- string initialGames
        psi.Environment.["TICTACTOE_MAX_GAMES"] <- string maxGames
        psi.Environment.["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] <- "1"
        psi.Environment.["ASPNETCORE_ENVIRONMENT"] <- "Development"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        let p = new Process(StartInfo = psi)
        // Drain stdout/stderr so the child never blocks on a full pipe buffer.
        p.OutputDataReceived.Add(fun _ -> ())
        p.ErrorDataReceived.Add(fun _ -> ())
        p.Start() |> ignore
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()
        p

    let port = freePort ()
    let baseUrl = sprintf "http://localhost:%d" port
    let proc = startProcess baseUrl initialGames maxGames

    do
        // Wait until the server answers (any HTTP status) or time out.
        use client = new HttpClient(BaseAddress = Uri(baseUrl))
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable ready = false
        while not ready && DateTime.UtcNow < deadline do
            let ok =
                try
                    client.GetAsync("/login").GetAwaiter().GetResult() |> ignore
                    true
                with _ -> false
            if ok then ready <- true else Thread.Sleep 500
        if not ready then
            (try proc.Kill(true) with _ -> ())
            failwithf "TicTacToe.Web did not become ready at %s within 60s" baseUrl

    member _.BaseUrl = baseUrl

    interface IDisposable with
        member _.Dispose() =
            (try (if not proc.HasExited then proc.Kill(true)) with _ -> ())
            proc.Dispose()
