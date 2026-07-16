namespace TicTacToe.Web.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading.Tasks
open System.Text.Json.Nodes
open NUnit.Framework
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

/// Dedicated coverage for the MCP surface merged into TicTacToe.Web (spec 003d): same engine, same
/// live game, same PlayerAssignmentManager/EventLog as HTTP — proven here with the REAL MCP client SDK
/// (the one real agents use), not raw HTTP calls, against a server this fixture owns end to end.
[<TestFixture>]
type McpTests() =
    let mutable server: ConfiguredServer option = None
    let mutable logPath = ""

    [<OneTimeSetUp>]
    member _.Setup() =
        logPath <- Path.Combine(Path.GetTempPath(), sprintf "mcp-test-log-%s.jsonl" (Guid.NewGuid().ToString("N")))
        server <- Some(new ConfiguredServer(1, mcpEnabled = true, requestLogPath = logPath))

    [<OneTimeTearDown>]
    member _.Teardown() =
        server |> Option.iter (fun s -> (s :> IDisposable).Dispose())
        server <- None
        if File.Exists logPath then (try File.Delete logPath with _ -> ())

    member private _.BaseUrl = (server |> Option.get).BaseUrl

    member private this.Connect() : Task<McpClient> =
        task {
            let transport =
                new HttpClientTransport(
                    HttpClientTransportOptions(Endpoint = Uri(this.BaseUrl + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp))
            return! McpClient.CreateAsync(transport)
        }

    member private _.CallText (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
        task {
            let! result = client.CallToolAsync(name, readOnlyDict args)
            return
                result.Content
                |> Seq.choose (fun c -> match c with :? TextContentBlock as t -> Some t.Text | _ -> None)
                |> String.concat "\n"
        }

    [<Test>]
    member this.``tools_list includes make_move and authenticate``() : Task =
        task {
            use! client = this.Connect()
            let! tools = client.ListToolsAsync()
            let names = tools |> Seq.map (fun t -> t.Name) |> Set.ofSeq
            Assert.That(names, Does.Contain "make_move")
            Assert.That(names, Does.Contain "authenticate")
            Assert.That(names, Does.Contain "list_games")
        }

    [<Test>]
    member this.``authenticate returns a non-empty token``() : Task =
        task {
            use! client = this.Connect()
            let! result = this.CallText client "authenticate" []
            let token = (JsonNode.Parse result).["token"].GetValue<string>()
            Assert.That(token, Is.Not.Null.And.Not.Empty)
        }

    [<Test>]
    member this.``a move made via MCP is visible through the HTTP interface (shared game state)``() : Task =
        task {
            use! client = this.Connect()
            let! authResult = this.CallText client "authenticate" []
            let token = (JsonNode.Parse authResult).["token"].GetValue<string>()
            let! gamesResult = this.CallText client "list_games" []
            let games = JsonNode.Parse gamesResult :?> JsonArray
            Assert.That(games.Count, Is.GreaterThan 0, "the seeded game must already be discoverable via list_games")
            let gameId = games.[0].["gameId"].GetValue<string>()

            let! moveResult =
                this.CallText client "make_move"
                    [ "gameId", box gameId; "position", box "TopLeft"; "identityToken", box token ]
            let moved = JsonNode.Parse moveResult
            Assert.That(moved.["error"], Is.Null, sprintf "make_move via MCP was rejected: %s" moveResult)

            // Same live game, other interface: a plain HTTP GET must show the move MCP just made.
            use handler = new HttpClientHandler(CookieContainer = CookieContainer(), AllowAutoRedirect = true)
            use http = new HttpClient(handler, BaseAddress = Uri(this.BaseUrl))
            let! _ = http.GetAsync("/login")
            let! page = http.GetStringAsync(sprintf "/games/%s" gameId)
            Assert.That(page, Does.Contain "TopLeft, X", "the MCP-made move must render through the HTTP surface")
        }

    [<Test>]
    member _.``player_assigned fires exactly once per seat, not once per move``() : Task =
        task {
            // A fresh server (fresh log) isolates this count from the other tests' moves.
            let path = Path.Combine(Path.GetTempPath(), sprintf "mcp-test-log-%s.jsonl" (Guid.NewGuid().ToString("N")))
            use isolated = new ConfiguredServer(1, mcpEnabled = true, requestLogPath = path)
            let transport =
                new HttpClientTransport(
                    HttpClientTransportOptions(Endpoint = Uri(isolated.BaseUrl + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp))
            use! client = McpClient.CreateAsync(transport)
            let callText (name: string) (args: (string * obj) list) =
                task {
                    let! result = client.CallToolAsync(name, readOnlyDict args)
                    return
                        result.Content
                        |> Seq.choose (fun c -> match c with :? TextContentBlock as t -> Some t.Text | _ -> None)
                        |> String.concat "\n"
                }
            let! authResult = callText "authenticate" []
            let token = (JsonNode.Parse authResult).["token"].GetValue<string>()
            let! gamesResult = callText "list_games" []
            let gameId = ((JsonNode.Parse gamesResult :?> JsonArray).[0]).["gameId"].GetValue<string>()

            // Same seat (same token) moves TWICE — X, then (rejected, not O's token) again — only the
            // FIRST call may bind the seat; a re-fire on the second would be exactly the churn bug
            // that cost a prior session real data.
            let! _ = callText "make_move" [ "gameId", box gameId; "position", box "TopLeft"; "identityToken", box token ]
            let! _ = callText "make_move" [ "gameId", box gameId; "position", box "TopCenter"; "identityToken", box token ]

            let lines = File.ReadAllLines path
            let assignedCount =
                lines
                |> Array.filter (fun l -> l.Contains "\"event_type\":\"player_assigned\"")
                |> Array.length
            Assert.That(assignedCount, Is.EqualTo 1, "player_assigned must fire exactly once per seat, never per move")
        }

/// MCP must stay off unless a caller explicitly asks for it — the app's default identity is the
/// Frank/Datastar demo (see TicTacToe.Web/Program.fs mcpEnabled()).
[<TestFixture>]
type McpDisabledByDefaultTests() =
    [<Test>]
    member _.``MCP endpoint is absent when TICTACTOE_MCP_ENABLED is unset``() : Task =
        task {
            use server = new ConfiguredServer(1)   // mcpEnabled NOT passed — the app's real default
            use handler = new HttpClientHandler()
            use http = new HttpClient(handler, BaseAddress = Uri(server.BaseUrl))
            let content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")
            content.Headers.ContentType <- Headers.MediaTypeHeaderValue("application/json")
            let! resp = http.PostAsync("/mcp", content)
            // Frank has no route at /mcp when MCP is off, so this must NOT succeed as an MCP handshake.
            Assert.That(resp.IsSuccessStatusCode, Is.False, "MCP must not respond when disabled by default")
        }
