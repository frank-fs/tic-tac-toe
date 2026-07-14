namespace TicTacToe.Web.Tests

// The wire facts the experiment's dependent variables are MADE of (spec 003b). Every one of these
// was produced by the retired Surface twin; the merged app owns them now, so they get a regression
// guard here rather than living only in the cross-app equivalence harness:
//
//   illegalMoves = 403 (out-of-turn / not-a-player) + 422 (position taken)
//   formatErrors = 400 (malformed move)
//   read friction = ETag + 304
//   Sd = /profile + Link rel=profile + /.well-known/home;  So = Link rel=describedby + ld+json 303
//
// Plus the alias invariant: /games and /arenas are ONE resource under two names.

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Threading.Tasks
open NUnit.Framework

/// One server PER TEST, on a chosen cell, with a single locked game — the experiment's own
/// configuration. Per-test (not per-fixture) because the one locked game accumulates seats and
/// moves: a shared server would make these tests order-dependent.
[<AbstractClass>]
type CellServerFixture(cell: string) =
    let mutable server: ConfiguredServer = Unchecked.defaultof<_>
    let mutable handler: HttpClientHandler = null
    let mutable client: HttpClient = null

    member _.Client = client
    member _.BaseUrl = server.BaseUrl

    /// A fresh cookie jar = a distinct user (identity is the cookie).
    member _.NewPlayer() : Task<HttpClient> =
        task {
            let h = new HttpClientHandler(CookieContainer = CookieContainer(), AllowAutoRedirect = false)
            let c = new HttpClient(h, BaseAddress = Uri(server.BaseUrl))
            let! _ = c.GetAsync "/login"
            return c
        }

    member this.Move (c: HttpClient) (path: string) (player: string) (position: string) : Task<HttpResponseMessage> =
        let body =
            new FormUrlEncodedContent([ KeyValuePair("player", player); KeyValuePair("position", position) ])
        c.PostAsync(path, body)

    /// The one game this server seeded (TICTACTOE_INITIAL_GAMES=1).
    member this.GameId() : Task<string> =
        task {
            let! home = client.GetStringAsync "/"
            let m = Text.RegularExpressions.Regex.Match(home, "games/([0-9a-f-]{36})")
            Assert.That(m.Success, Is.True, "the dashboard must link the seeded game")
            return m.Groups.[1].Value
        }

    [<SetUp>]
    member _.Start() =
        server <- new ConfiguredServer(1, maxGames = 1, cell = cell, lockGame = true)
        handler <- new HttpClientHandler(CookieContainer = CookieContainer(), AllowAutoRedirect = false)
        client <- new HttpClient(handler, BaseAddress = Uri(server.BaseUrl))
        client.GetAsync("/login").GetAwaiter().GetResult() |> ignore

    [<TearDown>]
    member _.Stop() =
        if not (isNull client) then client.Dispose()
        if not (isNull handler) then handler.Dispose()
        (server :> IDisposable).Dispose()


/// Full surface (1111): every factor on.
[<TestFixture>]
type FullSurfaceWireTests() =
    inherit CellServerFixture("1111")

    [<Test>]
    member this.``an accepted move answers 200 with the new board``() : Task =
        task {
            let! id = this.GameId()
            let! p1 = this.NewPlayer()
            use! resp = this.Move p1 $"/games/{id}" "X" "TopLeft"
            Assert.That(int resp.StatusCode, Is.EqualTo 200, "accepted move: 200 + representation")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "\"player\">X", "the response IS the new board")
        }

    [<Test>]
    member this.``a taken square is 422 — half of the illegalMoves DV``() : Task =
        task {
            let! id = this.GameId()
            let! x = this.NewPlayer()
            let! o = this.NewPlayer()
            use! _1 = this.Move x $"/games/{id}" "X" "MiddleCenter"      // X seats + plays
            use! taken = this.Move o $"/games/{id}" "O" "MiddleCenter"   // O plays the SAME square
            Assert.That(int taken.StatusCode, Is.EqualTo 422, "position taken must be 422, not 403/303")
        }

    [<Test>]
    member this.``an out-of-turn move is 403 — the other half``() : Task =
        task {
            let! id = this.GameId()
            let! x = this.NewPlayer()
            use! _1 = this.Move x $"/games/{id}" "X" "TopRight"          // X seats + plays; now O's turn
            use! again = this.Move x $"/games/{id}" "X" "BottomLeft"     // X moves again
            Assert.That(int again.StatusCode, Is.EqualTo 403, "out of turn must be 403")
        }

    [<Test>]
    member this.``a malformed move is 400 — the formatErrors DV``() : Task =
        task {
            let! id = this.GameId()
            let! p = this.NewPlayer()
            use! bad = this.Move p $"/games/{id}" "X" "Nowhere"
            Assert.That(int bad.StatusCode, Is.EqualTo 400, "unparseable position must be 400")
        }

    [<Test>]
    member this.``GET carries an ETag and a matching If-None-Match is 304``() : Task =
        task {
            let! id = this.GameId()
            use! first = this.Client.GetAsync $"/games/{id}"
            let etag = first.Headers.ETag
            Assert.That(etag, Is.Not.Null, "the board must carry an ETag (the cheap change-signal)")

            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{id}")
            req.Headers.TryAddWithoutValidation("If-None-Match", etag.ToString()) |> ignore
            use! second = this.Client.SendAsync req
            Assert.That(int second.StatusCode, Is.EqualTo 304, "an unchanged board must 304, not re-send")
        }

    [<Test>]
    member this.``Sd serves the ALPS profile and advertises it with a Link``() : Task =
        task {
            let! id = this.GameId()
            use! profile = this.Client.GetAsync "/profile"
            Assert.That(int profile.StatusCode, Is.EqualTo 200, "Sd=1 -> /profile 200")
            use! home = this.Client.GetAsync "/.well-known/home"
            Assert.That(int home.StatusCode, Is.EqualTo 200, "Sd=1 -> JSON Home 200")

            use! board = this.Client.GetAsync $"/games/{id}"
            let links = board.Headers.GetValues("Link") |> String.concat ", "
            Assert.That(links, Does.Contain "rel=\"profile\"", "the board must advertise the contract")
            // Allow is a CONTENT header in HttpClient's split of the header bag.
            Assert.That(board.Content.Headers.Allow.Count, Is.GreaterThan 0, "Sd=1 -> state-dependent Allow")
        }

    [<Test>]
    member this.``So types the game: describedby Link, ld+json 303, JSON-LD document``() : Task =
        task {
            let! id = this.GameId()
            use! board = this.Client.GetAsync $"/games/{id}"
            let links = board.Headers.GetValues("Link") |> String.concat ", "
            Assert.That(links, Does.Contain "rel=\"describedby\"", "So=1 -> describedby Link")

            // httpRange-14: asking the THING for RDF redirects to the DOCUMENT that describes it.
            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{id}")
            req.Headers.TryAddWithoutValidation("Accept", "application/ld+json") |> ignore
            use! conneg = this.Client.SendAsync req
            Assert.That(int conneg.StatusCode, Is.EqualTo 303, "ld+json on the game must 303 to /type")
            Assert.That(conneg.Headers.Location.ToString(), Does.Contain $"/games/{id}/type")

            use! typed = this.Client.GetAsync $"/games/{id}/type"
            let! body = typed.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "\"@type\": \"Game\"", "the description is schema.org/Game JSON-LD")
        }

    [<Test>]
    member this.``a locked game refuses reset and delete with 409``() : Task =
        task {
            let! id = this.GameId()
            let! p = this.NewPlayer()
            use! reset = p.PostAsync($"/games/{id}/reset", new StringContent(""))
            Assert.That(int reset.StatusCode, Is.EqualTo 409, "TICTACTOE_LOCK_GAME -> no reset")
            use! del = p.PostAsync($"/games/{id}/delete", new StringContent(""))
            Assert.That(int del.StatusCode, Is.EqualTo 409, "TICTACTOE_LOCK_GAME -> no delete")
        }

    [<Test>]
    member this.``the SSE stream is never handed to a non-streaming client``() : Task =
        task {
            let! id = this.GameId()
            // A plain GET would otherwise hang on an open stream until the caller's own timeout.
            use! resp = this.Client.GetAsync $"/games/{id}/sse"
            Assert.That(int resp.StatusCode, Is.EqualTo 406, "a non-streaming caller gets 406, not a stream")
            let links = resp.Headers.GetValues("Link") |> String.concat ", "
            Assert.That(links, Does.Contain $"/games/{id}", "and is pointed at the resource that answers a GET")
        }

    [<Test>]
    member this.``/arenas is the same resource under another name``() : Task =
        task {
            let! id = this.GameId()
            let! p = this.NewPlayer()
            // The banked surface: play through /arenas, read back through /games.
            use! move = this.Move p $"/arenas/{id}" "X" "BottomRight"
            Assert.That(int move.StatusCode, Is.EqualTo 200, "/arenas accepts the move")
            let! body = move.Content.ReadAsStringAsync()
            // The representation stays on the name it was served as: links and control forms too.
            Assert.That(body, Does.Contain $"href=\"/arenas/{id}\"", "the canonical link stays on /arenas")
            Assert.That(body, Does.Contain $"action=\"/arenas/{id}/reset\"", "and so do its forms")
            Assert.That(body, Does.Contain $"href=\"/games/{id}\"", "with the /games alias advertised")

            use! viaGames = this.Client.GetAsync $"/games/{id}"
            let! gamesBody = viaGames.Content.ReadAsStringAsync()
            Assert.That(gamesBody, Does.Contain "\"player\">X", "the move is visible through the alias")

            use! typed = this.Client.GetAsync $"/arenas/{id}/type"
            Assert.That(int typed.StatusCode, Is.EqualTo 200, "/arenas/{id}/type serves the ontology too")
        }


/// The discovery floor (0000): the wire is identical, the SURFACE is bare.
[<TestFixture>]
type FloorSurfaceWireTests() =
    inherit CellServerFixture("0000")

    [<Test>]
    member this.``Sd=0 hides the contract: /profile and JSON Home 404``() : Task =
        task {
            use! profile = this.Client.GetAsync "/profile"
            Assert.That(int profile.StatusCode, Is.EqualTo 404, "Sd=0 -> no contract to fetch")
            use! home = this.Client.GetAsync "/.well-known/home"
            Assert.That(int home.StatusCode, Is.EqualTo 404, "Sd=0 -> no JSON Home")
        }

    [<Test>]
    member this.``So=0 hides the ontology: no describedby, no conneg, /type 404``() : Task =
        task {
            let! id = this.GameId()
            use! board = this.Client.GetAsync $"/games/{id}"
            let links =
                if board.Headers.Contains "Link" then board.Headers.GetValues("Link") |> String.concat ", " else ""
            Assert.That(links, Does.Not.Contain "describedby", "So=0 -> nothing is typed")

            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{id}")
            req.Headers.TryAddWithoutValidation("Accept", "application/ld+json") |> ignore
            use! conneg = this.Client.SendAsync req
            Assert.That(int conneg.StatusCode, Is.EqualTo 200, "So=0 -> no 303; just the HTML")

            use! typed = this.Client.GetAsync $"/games/{id}/type"
            Assert.That(int typed.StatusCode, Is.EqualTo 404, "So=0 -> no describing document")
        }

    [<Test>]
    member this.``the floor still ships ETag/304 — hygiene is never a factor``() : Task =
        task {
            // If read-friction hygiene rode on a factor it would confound every cross-cell number.
            let! id = this.GameId()
            use! first = this.Client.GetAsync $"/games/{id}"
            Assert.That(first.Headers.ETag, Is.Not.Null, "ETag is baseline on ALL cells")
            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{id}")
            req.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag.ToString()) |> ignore
            use! second = this.Client.SendAsync req
            Assert.That(int second.StatusCode, Is.EqualTo 304, "304 on ALL cells")
        }

    [<Test>]
    member this.``the floor keeps the same illegal-move wire (422/403/400)``() : Task =
        task {
            let! id = this.GameId()
            let! x = this.NewPlayer()
            let! o = this.NewPlayer()
            use! ok = this.Move x $"/games/{id}" "X" "MiddleCenter"
            Assert.That(int ok.StatusCode, Is.EqualTo 200, "accepted")
            use! taken = this.Move o $"/games/{id}" "O" "MiddleCenter"
            Assert.That(int taken.StatusCode, Is.EqualTo 422, "taken square: 422 on the floor too")
            use! outOfTurn = this.Move x $"/games/{id}" "X" "TopLeft"
            Assert.That(int outOfTurn.StatusCode, Is.EqualTo 403, "out of turn: 403 on the floor too")
            use! bad = this.Move x $"/games/{id}" "X" "Nowhere"
            Assert.That(int bad.StatusCode, Is.EqualTo 400, "malformed: 400 on the floor too")
        }

    [<Test>]
    member this.``the floor page carries no aria/role and nine ungated forms``() : Task =
        task {
            let! id = this.GameId()
            let! board = this.Client.GetStringAsync $"/games/{id}"
            Assert.That(board, Does.Not.Contain "aria-", "C=0 -> no aria anywhere")
            Assert.That(board, Does.Not.Contain "role=\"", "C=0 -> no role anywhere")
            let forms = Text.RegularExpressions.Regex.Matches(board, "<form[^>]*method=\"post\"").Count
            Assert.That(forms, Is.EqualTo 11, "A=0 -> 9 square forms + reset + delete (the twin's surface)")
        }
