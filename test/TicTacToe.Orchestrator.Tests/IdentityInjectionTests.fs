module TicTacToe.Orchestrator.Tests.IdentityInjectionTests

// Integration acceptance for #67 Task 8: proves the ORCHESTRATOR's own McpClient code
// SENDS each agent's identity in MCP `_meta` (not in args) over ONE shared stdio
// connection. Launches the real identity-aware mcp-rpc server (Release dll) and drives
// it through the orchestrator's McpClientSet identity-scoped call path:
//   authenticate x2 -> distinct tokens A, B
//   new_game -> gameId
//   make_move(_meta=A, TopLeft)   -> board[0]="X"  (A binds to X)
//   make_move(_meta=B, TopCenter) -> board[1]="O"  (B is a DISTINCT seat -> O)
// A and B being distinct seats over the one connection is the proof.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading
open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.McpClient

let private findRepoRoot () : string =
    // Walk up from the test assembly location until experiments/mcp-rpc exists.
    let rec up (dir: DirectoryInfo) (depth: int) =
        if depth <= 0 then failwith "could not locate repo root"
        elif Directory.Exists(Path.Combine(dir.FullName, "experiments", "mcp-rpc")) then dir.FullName
        else up dir.Parent (depth - 1)
    up (DirectoryInfo(AppContext.BaseDirectory)) 12

let private erpcServerConfig (repoRoot: string) : McpServerConfig =
    let dll =
        Path.Combine(repoRoot, "experiments", "mcp-rpc", "bin", "Release", "net10.0", "TicTacToe.McpRpc.dll")
    if not (File.Exists dll) then
        failwith $"identity server dll not found: {dll} (build Release first: dotnet build experiments/mcp-rpc -c Release)"
    { Name = "tictactoe-mcp"; Command = "dotnet"; Arguments = [| dll |] }

let private parse (json: string) : JsonObject = JsonNode.Parse(json) :?> JsonObject

let private token (json: string) : string = (parse json).["token"].GetValue<string>()

let private boardCell (json: string) (idx: int) : string =
    let obj = parse json
    Assert.That(obj.ContainsKey("error"), Is.False, $"expected success, got: {json}")
    let board = obj.["board"] :?> JsonArray
    board.[idx].GetValue<string>()

[<Test>]
[<CancelAfter(120000)>]
let ``orchestrator injects per-agent _meta identity over one shared connection`` () =
    let repoRoot = findRepoRoot ()
    let clients = new McpClientSet([ erpcServerConfig repoRoot ])
    use _ = clients :> IDisposable
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 90.0)
    let ct = cts.Token

    clients.InitializeAsync(ct).GetAwaiter().GetResult()

    let tokenA = token (clients.CallToolAsync("authenticate", Map.empty, None, ct).GetAwaiter().GetResult())
    let tokenB = token (clients.CallToolAsync("authenticate", Map.empty, None, ct).GetAwaiter().GetResult())
    Assert.That(tokenB, Is.Not.EqualTo(tokenA), "authenticate must mint distinct tokens")

    let gameId =
        (parse (clients.CallToolAsync("new_game", Map.empty, None, ct).GetAwaiter().GetResult()))
            .["gameId"].GetValue<string>()

    let move (pos: string) (idToken: string) : string =
        let args =
            Map.ofList
                [ "gameId", (JsonValue.Create gameId :> JsonNode)
                  "position", (JsonValue.Create pos :> JsonNode) ]
        clients.CallToolAsync("make_move", args, Some idToken, ct).GetAwaiter().GetResult()

    // A's identity (via _meta) binds to X; X moves first.
    let resA = move "TopLeft" tokenA
    TestContext.Out.WriteLine($"A(_meta={tokenA.Substring(0, 8)}..) make_move TopLeft -> {resA}")
    Assert.That(boardCell resA 0, Is.EqualTo("X"), $"A should bind to X at TopLeft: {resA}")

    // B's DISTINCT identity (via _meta) binds to O over the SAME connection.
    let resB = move "TopCenter" tokenB
    TestContext.Out.WriteLine($"B(_meta={tokenB.Substring(0, 8)}..) make_move TopCenter -> {resB}")
    Assert.That(boardCell resB 1, Is.EqualTo("O"), $"B should bind to O at TopCenter: {resB}")
