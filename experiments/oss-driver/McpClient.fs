module TicTacToe.OssDriver.McpClient

// Launch the ERPC MCP server (mcp-rpc) as a stdio subprocess and expose list/call, so the
// ONE driver can drive the RPC null-hypothesis arm too. The model calls these tools; identity
// is the model's (authenticate once -> pass identityToken on make_move), same discipline as the
// HTTP cold-start. stdout is the JSON-RPC channel; the server logs to stderr.

open System
open System.Collections.Generic
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

/// Grab a free loopback TCP port (bind :0, read it back, release). Each multiplayer run picks its own port
/// so a leaked/slow-dying server from a prior run can never contaminate the next (fixed-port cross-run bug).
let freePort () : int =
    let l = TcpListener(IPAddress.Loopback, 0)
    l.Start()
    let p = (l.LocalEndpoint :?> IPEndPoint).Port
    l.Stop()
    p

let private noArgs : IReadOnlyDictionary<string, obj> = readOnlyDict ([]: (string * obj) list)

/// Pull the text out of an MCP tool result (shared by the stdio + HTTP clients).
let private toolText (r: ModelContextProtocol.Protocol.CallToolResult) : string =
    r.Content
    |> Seq.choose (fun c -> match c with | :? TextContentBlock as t -> Some t.Text | _ -> None)
    |> String.concat "\n"

type Erpc(serverDll: string, workdir: string, env: IReadOnlyDictionary<string, string>) =
    let opts =
        StdioClientTransportOptions(
            Name = "erpc", Command = "dotnet", Arguments = [| serverDll |], WorkingDirectory = workdir)
    let transport =
        let envDict = Dictionary<string, string>()
        for kv in env do envDict.[kv.Key] <- kv.Value
        opts.EnvironmentVariables <- envDict
        new StdioClientTransport(opts)
    let client = McpClient.CreateAsync(transport).GetAwaiter().GetResult()

    member _.Tools : IList<McpClientTool> = client.ListToolsAsync().GetAwaiter().GetResult()

    member _.Call (name: string) (args: IReadOnlyDictionary<string, obj>) : string =
        toolText (client.CallToolAsync(name, args).GetAwaiter().GetResult())

    interface IDisposable with
        member _.Dispose() = (try (client :> IAsyncDisposable).DisposeAsync().AsTask().Wait() with _ -> ())

/// Launch the mcp-rpc server in HTTP mode (`--http <url>`) as a child process so MANY clients can connect
/// with their own sessions (the multiplayer arm). `dotnet <dll>` runs in the host process itself, so
/// killing the returned Process stops the server. stderr is captured; stdout is unused (HTTP, not stdio).
let launchHttpServer (serverDll: string) (workdir: string) (url: string) (env: (string * string) list) : Process =
    // Inherit the parent's stdout/stderr (no redirect): the server's logs/exceptions flow to the driver's
    // console, and NOT draining a redirected pipe would deadlock the server once its stderr buffer fills.
    let psi =
        ProcessStartInfo(
            FileName = "dotnet", WorkingDirectory = workdir, UseShellExecute = false)
    psi.ArgumentList.Add serverDll
    psi.ArgumentList.Add "--http"
    psi.ArgumentList.Add url
    for (k, v) in env do psi.Environment.[k] <- v
    Process.Start psi

/// Poll the HTTP host until it answers (any status = up) or the bounded try budget is spent (R10). A GET to
/// the MCP endpoint returns 4xx/405 when ready and throws on connection-refused while still starting.
let waitHttpReady (url: string) (tries: int) : bool =
    use http = new HttpClient(Timeout = TimeSpan.FromSeconds 2.0)
    let rec loop i =
        if i >= tries then false
        else
            let ok = try (http.GetAsync(url).GetAwaiter().GetResult() |> ignore; true) with _ -> false
            if ok then true
            else System.Threading.Thread.Sleep 250; loop (i + 1)
    loop 0

/// One HTTP/streamable MCP client = ONE session. In multiplayer each agent owns one of these, so the
/// server recognizes callers independently (the fair analog of the HTTP arm's separate seat processes).
type ErpcHttp(url: string) =
    let transport =
        new HttpClientTransport(
            HttpClientTransportOptions(Endpoint = Uri url, TransportMode = HttpTransportMode.StreamableHttp))
    let client = McpClient.CreateAsync(transport).GetAwaiter().GetResult()

    member _.Tools : IList<McpClientTool> = client.ListToolsAsync().GetAwaiter().GetResult()

    member _.Call (name: string) (args: IReadOnlyDictionary<string, obj>) : string =
        toolText (client.CallToolAsync(name, args).GetAwaiter().GetResult())

    interface IDisposable with
        member _.Dispose() = (try (client :> IAsyncDisposable).DisposeAsync().AsTask().Wait() with _ -> ())

/// No-LLM smoke: prove the client can list + call tools (authenticate -> new_game -> make_move).
let private noEnv : IReadOnlyDictionary<string, string> = readOnlyDict ([]: (string * string) list)

let smoke (serverDll: string) (workdir: string) : int =
    use e = new Erpc(serverDll, workdir, noEnv)
    eprintfn "=== tools ==="
    for t in e.Tools do eprintfn "  %s — %s" t.Name t.Description
    let prop (s: string) (k: string) = JsonDocument.Parse(s).RootElement.GetProperty(k).GetString()
    let token = prop (e.Call "authenticate" noArgs) "token"
    eprintfn "token=%s" token
    let gid = prop (e.Call "new_game" noArgs) "gameId"
    eprintfn "gameId=%s" gid
    let mv = e.Call "make_move" (readOnlyDict [ "gameId", box gid; "position", box "MiddleCenter"; "identityToken", box token ])
    eprintfn "make_move(MiddleCenter) -> %s" mv
    0

/// No-LLM HTTP smoke: launch the multi-client HTTP host and drive TWO independent sessions to prove seats
/// are assigned by move order, each session is a distinct identity, and moves actually land (board changes).
let smokeHttp (serverDll: string) (workdir: string) : int =
    let url = "http://127.0.0.1:6349/"
    use server = launchHttpServer serverDll workdir url [ "TICTACTOE_REQUEST_LOG_PATH", "/tmp/erpc-httpsmoke.jsonl" ]
    if not (waitHttpReady url 60) then eprintfn "server not ready"; server.Kill true; exit 1
    use setup = new ErpcHttp(url)
    let gid = JsonDocument.Parse(setup.Call "new_game" noArgs).RootElement.GetProperty("gameId").GetString()
    eprintfn "gameId=%s" gid
    use a = new ErpcHttp(url)
    use b = new ErpcHttp(url)
    let tok (c: ErpcHttp) = JsonDocument.Parse(c.Call "authenticate" noArgs).RootElement.GetProperty("token").GetString()
    let ta = tok a
    let tb = tok b
    eprintfn "A token=%s  B token=%s  (distinct=%b)" ta tb (ta <> tb)
    let mv (c: ErpcHttp) tkn pos = c.Call "make_move" (readOnlyDict [ "gameId", box gid; "position", box pos; "identityToken", box tkn ])
    eprintfn "A MiddleCenter -> %s" (mv a ta "MiddleCenter")   // A moves first on X's turn -> seats A as X
    eprintfn "B TopLeft      -> %s" (mv b tb "TopLeft")        // B moves next  on O's turn -> seats B as O
    eprintfn "A TopRight     -> %s" (mv a ta "TopRight")       // X again
    eprintfn "B BottomLeft   -> %s" (mv b tb "BottomLeft")     // O again
    server.Kill true
    0
