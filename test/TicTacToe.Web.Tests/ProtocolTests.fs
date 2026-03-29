namespace TicTacToe.Web.Tests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open NUnit.Framework

/// Protocol-level tests for Frank 7.3.0 discovery and statechart features.
/// Tests HTTP protocol semantics (OPTIONS, Allow, JSON Home, 405, 403, agent auth)
/// without browser interaction. Requires a running server on port 5228.
[<TestFixture>]
type ProtocolTests() =
    let mutable client: HttpClient = null
    let mutable handler: HttpClientHandler = null

    let baseUrl =
        Environment.GetEnvironmentVariable("TEST_BASE_URL")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty(s)))
        |> Option.defaultValue "http://localhost:5000"

    [<OneTimeSetUp>]
    member _.Setup() =
        handler <- new HttpClientHandler(
            CookieContainer = CookieContainer(),
            AllowAutoRedirect = true
        )
        client <- new HttpClient(handler, BaseAddress = Uri(baseUrl))

    [<OneTimeTearDown>]
    member _.Teardown() =
        if not (isNull client) then
            client.Dispose()
            client <- null
        if not (isNull handler) then
            handler.Dispose()
            handler <- null

    /// Helper to ensure client is authenticated with a cookie
    member private _.EnsureAuthenticated() : Task =
        task {
            let! _ = client.GetAsync("/login")
            ()
        }

    /// Helper to create a game and return its URL and ID
    member private this.CreateGame() : Task<string * string> =
        task {
            do! this.EnsureAuthenticated()
            let! response = client.PostAsync("/games", null)
            let gameUrl = response.Headers.Location.ToString()
            let gameId = gameUrl.Substring("/games/".Length)
            return (gameUrl, gameId)
        }

    /// Helper to make a move using datastar signals format
    member private _.MakeMove(httpClient: HttpClient, gameUrl: string, gameId: string, player: string, position: string) : Task<HttpResponseMessage> =
        task {
            let signals = sprintf """{"gameId":"%s","player":"%s","position":"%s"}""" gameId player position
            let content = new StringContent(signals, Text.Encoding.UTF8, "application/json")
            content.Headers.ContentType.MediaType <- "application/json"

            use request = new HttpRequestMessage(HttpMethod.Post, gameUrl, Content = content)
            request.Headers.Add("datastar-request", "true")

            return! httpClient.SendAsync(request)
        }

    /// Helper to play a game to completion (X wins via top row).
    /// Uses two separate clients (two users) to alternate moves.
    /// Returns the game URL and ID.
    member private this.PlayGameToCompletion() : Task<string * string> =
        task {
            let! (gameUrl, gameId) = this.CreateGame()

            // Player X (this.client) makes moves
            // Player O needs a separate client (separate cookie jar = separate user)
            use oHandler = new HttpClientHandler(CookieContainer = CookieContainer(), AllowAutoRedirect = true)
            use oClient = new HttpClient(oHandler, BaseAddress = Uri(baseUrl))
            let! _ = oClient.GetAsync("/login")

            // X: TopLeft
            let! r1 = this.MakeMove(client, gameUrl, gameId, "X", "TopLeft")
            Assert.That(int r1.StatusCode, Is.EqualTo(202), "X move TopLeft should succeed")

            // O: MiddleLeft
            let! r2 = this.MakeMove(oClient, gameUrl, gameId, "O", "MiddleLeft")
            Assert.That(int r2.StatusCode, Is.EqualTo(202), "O move MiddleLeft should succeed")

            // X: TopCenter
            let! r3 = this.MakeMove(client, gameUrl, gameId, "X", "TopCenter")
            Assert.That(int r3.StatusCode, Is.EqualTo(202), "X move TopCenter should succeed")

            // O: MiddleCenter
            let! r4 = this.MakeMove(oClient, gameUrl, gameId, "O", "MiddleCenter")
            Assert.That(int r4.StatusCode, Is.EqualTo(202), "O move MiddleCenter should succeed")

            // X: TopRight (X wins with top row)
            let! r5 = this.MakeMove(client, gameUrl, gameId, "X", "TopRight")
            Assert.That(int r5.StatusCode, Is.EqualTo(202), "X move TopRight should succeed")

            return (gameUrl, gameId)
        }

    // ============================================================================
    // Test 1: OPTIONS during active play returns Allow with GET, POST, DELETE
    // ============================================================================

    [<Test>]
    member this.``OPTIONS on active game returns Allow header with GET POST DELETE``() : Task =
        task {
            let! (gameUrl, _gameId) = this.CreateGame()

            use request = new HttpRequestMessage(HttpMethod.Options, gameUrl)
            let! response = client.SendAsync(request)

            // OPTIONS should succeed
            Assert.That(int response.StatusCode, Is.AnyOf(200, 204), "OPTIONS should return 200 or 204")

            // Check Allow header
            Assert.That(response.Content.Headers.Allow, Is.Not.Null.And.Not.Empty, "Allow header should be present")
            let allowedMethods = response.Content.Headers.Allow |> Seq.map (fun m -> m.ToUpperInvariant()) |> Set.ofSeq
            Assert.That(allowedMethods, Does.Contain("GET"), "Allow should contain GET")
            Assert.That(allowedMethods, Does.Contain("POST"), "Allow should contain POST")
            Assert.That(allowedMethods, Does.Contain("DELETE"), "Allow should contain DELETE")
        }

    // ============================================================================
    // Test 2: OPTIONS after game ends returns reduced Allow (no POST)
    // ============================================================================

    [<Test>]
    member this.``OPTIONS on finished game returns Allow without POST``() : Task =
        task {
            let! (gameUrl, _gameId) = this.PlayGameToCompletion()

            use request = new HttpRequestMessage(HttpMethod.Options, gameUrl)
            let! response = client.SendAsync(request)

            Assert.That(int response.StatusCode, Is.AnyOf(200, 204), "OPTIONS should return 200 or 204")

            Assert.That(response.Content.Headers.Allow, Is.Not.Null.And.Not.Empty, "Allow header should be present")
            let allowedMethods = response.Content.Headers.Allow |> Seq.map (fun m -> m.ToUpperInvariant()) |> Set.ofSeq
            Assert.That(allowedMethods, Does.Contain("GET"), "Allow should contain GET")
            Assert.That(allowedMethods, Does.Contain("DELETE"), "Allow should contain DELETE")
            Assert.That(allowedMethods, Does.Not.Contain("POST"), "Allow should NOT contain POST for finished game")
        }

    // ============================================================================
    // Test 3: GET / with Accept: application/json-home returns JSON Home
    // ============================================================================

    [<Test>]
    member this.``GET root with json-home Accept returns JSON Home document``() : Task =
        task {
            do! this.EnsureAuthenticated()

            use request = new HttpRequestMessage(HttpMethod.Get, "/")
            request.Headers.Add("Accept", "application/json-home")

            let! response = client.SendAsync(request)

            Assert.That(int response.StatusCode, Is.EqualTo(200), "Should return 200 OK")

            let contentType = response.Content.Headers.ContentType.ToString()
            Assert.That(contentType, Does.Contain("application/json-home"), "Content-Type should be application/json-home")

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("resources"), "JSON Home body should contain 'resources' key")
        }

    // ============================================================================
    // Test 4: GET / with Accept: text/html returns normal HTML
    // ============================================================================

    [<Test>]
    member this.``GET root with html Accept returns HTML page``() : Task =
        task {
            do! this.EnsureAuthenticated()

            use request = new HttpRequestMessage(HttpMethod.Get, "/")
            request.Headers.Add("Accept", "text/html")

            let! response = client.SendAsync(request)

            Assert.That(int response.StatusCode, Is.EqualTo(200), "Should return 200 OK")

            let contentType = response.Content.Headers.ContentType.ToString()
            Assert.That(contentType, Does.Contain("text/html"), "Content-Type should be text/html")

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("<html").Or.Contain("<!DOCTYPE").IgnoreCase, "Body should contain HTML")
        }

    // ============================================================================
    // Test 5: 405 Method Not Allowed when POSTing to finished game
    // ============================================================================

    [<Test>]
    member this.``POST to finished game returns 405 Method Not Allowed``() : Task =
        task {
            let! (gameUrl, gameId) = this.PlayGameToCompletion()

            // Attempt to POST a move to the finished game
            let! response = this.MakeMove(client, gameUrl, gameId, "X", "BottomLeft")

            Assert.That(int response.StatusCode, Is.EqualTo(405), "Should return 405 Method Not Allowed for POST to finished game")
        }

    // ============================================================================
    // Test 6: 403 Forbidden when wrong player attempts a move
    // ============================================================================

    [<Test>]
    member this.``Wrong player attempting move returns 403 Forbidden``() : Task =
        task {
            let! (gameUrl, gameId) = this.CreateGame()

            // Player X makes first move (claims X slot)
            let! r1 = this.MakeMove(client, gameUrl, gameId, "X", "TopLeft")
            Assert.That(int r1.StatusCode, Is.EqualTo(202), "First move should succeed")

            // Same player (X) attempts second move (it's O's turn now)
            let! r2 = this.MakeMove(client, gameUrl, gameId, "O", "MiddleCenter")

            // Should be rejected: either 403 from handler (assignment validation)
            // or 403 from statechart guard (turn order via claims)
            Assert.That(int r2.StatusCode, Is.EqualTo(403), "Wrong player should get 403 Forbidden")
        }

    // ============================================================================
    // Test 7: X-Agent-Id header creates authenticated identity
    // ============================================================================

    [<Test>]
    member this.``X-Agent-Id header authenticates agent without cookie``() : Task =
        task {
            // Create a game with the cookie-authenticated client
            let! (gameUrl, _gameId) = this.CreateGame()

            // Create a fresh client WITHOUT cookies (no auto-redirect to avoid login redirect)
            use agentHandler = new HttpClientHandler(CookieContainer = CookieContainer(), AllowAutoRedirect = false)
            use agentClient = new HttpClient(agentHandler, BaseAddress = Uri(baseUrl))

            // Send GET with X-Agent-Id header (no cookie)
            use request = new HttpRequestMessage(HttpMethod.Get, gameUrl)
            request.Headers.Add("X-Agent-Id", "test-agent-001")

            let! response = agentClient.SendAsync(request)

            // Should be 200 (authenticated via agent header, not redirected to login)
            Assert.That(int response.StatusCode, Is.EqualTo(200), "Agent with X-Agent-Id should get 200, not a redirect or 401")
        }
