namespace TicTacToe.Web.Tests

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open NUnit.Framework

/// Phase A E2E for the "Proto progressive enhancement" milestone (spec:
/// docs/superpowers/specs/2026-06-15-proto-same-url-conneg-pe-design.md) plus the
/// expert-review follow-ups that close the no-JS WRITE loop.
///
/// Proves the no-JS html surface: server-rendered discovery, a navigable link trail,
/// no-JS create/move/delete via real forms with Post/Redirect/Get, refresh-to-update
/// freshness, no-cache on dynamic responses, and a11y.
///
/// Raw HttpClient (NOT Playwright) on purpose: Playwright executes the Datastar JS, so it
/// cannot prove the page is usable with no JS. AllowAutoRedirect=false so PRG 303s are visible.
[<TestFixture>]
type ProtoPeServerRenderTests() =
    let mutable client: HttpClient = null
    let mutable handler: HttpClientHandler = null

    let baseUrl =
        Environment.GetEnvironmentVariable("TEST_BASE_URL")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty(s)))
        |> Option.defaultValue "http://localhost:5000"

    let postForm (url: string) (fields: (string * string) list) : Task<HttpResponseMessage> =
        let content = new FormUrlEncodedContent(fields |> List.map (fun (k, v) -> KeyValuePair(k, v)))
        client.PostAsync(url, content)

    [<OneTimeSetUp>]
    member _.Setup() : Task =
        task {
            // No auto-redirect: a 302 to /login still populates the cookie container, and
            // PRG 303s on writes stay visible for assertion.
            handler <- new HttpClientHandler(CookieContainer = Net.CookieContainer(), AllowAutoRedirect = false)
            client <- new HttpClient(handler, BaseAddress = Uri(baseUrl))
            let! _ = client.GetAsync("/login")
            ()
        }

    [<OneTimeTearDown>]
    member _.Teardown() =
        if not (isNull client) then client.Dispose(); client <- null
        if not (isNull handler) then handler.Dispose(); handler <- null

    /// Create a game via the datastar/API path (no form content type) -> 201 + Location.
    member private _.CreateGame() : Task<string> =
        task {
            let! resp = client.PostAsync("/games", null)
            let loc = resp.Headers.Location.ToString()
            return loc.Substring("/games/".Length)
        }

    // ── E2E #1 — no-JS discovery on GET / ───────────────────────────────────────
    [<Test>]
    member this.``GET / server-renders all active games with position forms``() : Task =
        task {
            let! gameId = this.CreateGame()
            use! resp = client.GetAsync("/")
            Assert.That(int resp.StatusCode, Is.EqualTo 200)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "game-board", "no-JS GET / must contain game boards")
            Assert.That(body, Does.Contain $"game-{gameId}", "must contain the created game's board")
            Assert.That(body, Does.Contain "action=\"/games/", "boards must carry playable position forms")
            Assert.That(body, Does.Not.Contain "Connecting...", "must not be the empty SSE-loading shell")
        }

    // ── New Game is a real no-JS form ───────────────────────────────────────────
    [<Test>]
    member _.``GET / offers a no-JS New Game form``() : Task =
        task {
            use! resp = client.GetAsync("/")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "method=\"post\"", "New Game must be a real form")
            Assert.That(body, Does.Contain "action=\"/games\"", "form must POST to /games (no-JS create)")
        }

    [<Test>]
    member _.``no-JS form POST to /games creates a game and redirects (PRG)``() : Task =
        task {
            use! resp = postForm "/games" []
            Assert.That(int resp.StatusCode, Is.EqualTo 303, "no-JS create must Post/Redirect/Get")
            Assert.That(resp.Headers.Location.ToString(), Is.EqualTo "/", "redirect to the dashboard")
        }

    // ── E2E #2 — html representation of a game ──────────────────────────────────
    [<Test>]
    member this.``GET /games/{id} server-renders the board``() : Task =
        task {
            let! gameId = this.CreateGame()
            use! resp = client.GetAsync($"/games/{gameId}")
            Assert.That(int resp.StatusCode, Is.EqualTo 200)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "game-board", "the game's html must be the server-rendered board")
        }

    // ── Navigable discovery trail: board links to its canonical URL ─────────────
    [<Test>]
    member this.``rendered board links to its canonical /games/{id} URL``() : Task =
        task {
            let! gameId = this.CreateGame()
            use! resp = client.GetAsync("/")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain $"href=\"/games/{gameId}\"", "board must carry a navigable link to its URL")
        }

    // ── E2E #5 / #14 — no-JS form move mutates state; refresh shows it ──────────
    [<Test>]
    member this.``no-JS form move applies and a fresh GET shows the mark``() : Task =
        task {
            let! gameId = this.CreateGame()
            use! moveResp = postForm $"/games/{gameId}" [ "player", "X"; "position", "TopLeft" ]
            Assert.That(int moveResp.StatusCode, Is.EqualTo 303, "no-JS move must Post/Redirect/Get")
            Assert.That(moveResp.Headers.Location.ToString(), Is.EqualTo $"/games/{gameId}")

            use! resp = client.GetAsync($"/games/{gameId}")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "\"player\">X", "a fresh GET must show the placed X mark")
        }

    // ── E2E #6 — a11y: position-labeled, action-named controls + live region ────
    [<Test>]
    member this.``rendered board carries aria-live and action-named position controls``() : Task =
        task {
            let! gameId = this.CreateGame()
            use! resp = client.GetAsync($"/games/{gameId}")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "aria-live", "boards must carry an aria-live region")
            Assert.That(body, Does.Contain "role=\"status\"", "status must be a role=status live region (works no-JS too)")
            Assert.That(body, Does.Contain "aria-label=\"Play X at", "controls must name the action, not just the position")
        }

    // ── Dynamic, per-viewer responses must not be cached ───────────────────────
    [<Test>]
    member this.``GET / and GET /games/{id} are no-cache``() : Task =
        task {
            let! gameId = this.CreateGame()
            use! home = client.GetAsync("/")
            use! game = client.GetAsync($"/games/{gameId}")
            Assert.That(home.Headers.CacheControl.NoCache, Is.True, "GET / must be no-cache")
            Assert.That(game.Headers.CacheControl.NoCache, Is.True, "GET /games/{id} must be no-cache")
        }

    // ── 404 is self-descriptive html with a way home ───────────────────────────
    [<Test>]
    member _.``GET unknown game returns 404 html with a link home``() : Task =
        task {
            use! resp = client.GetAsync("/games/does-not-exist")
            Assert.That(int resp.StatusCode, Is.EqualTo 404)
            Assert.That(resp.Content.Headers.ContentType.MediaType, Is.EqualTo "text/html")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "href=\"/\"", "404 must offer a navigation affordance home")
        }

    // ── E2E #5 (opponent) — a second user's move shows on a fresh dashboard GET ──
    // The research-critical case: player A loads, player B moves, A refreshes / and sees it.
    // Proves refresh-to-update across users on the dashboard, with no JS.
    [<Test>]
    member this.``opponent move is visible on a fresh no-JS dashboard GET``() : Task =
        task {
            let! gameId = this.CreateGame()
            // Player B: a separate cookie jar = a distinct user.
            use bHandler = new HttpClientHandler(CookieContainer = Net.CookieContainer(), AllowAutoRedirect = false)
            use bClient = new HttpClient(bHandler, BaseAddress = Uri(baseUrl))
            let! _ = bClient.GetAsync("/login")
            let bMove = new FormUrlEncodedContent([ KeyValuePair("player", "X"); KeyValuePair("position", "TopLeft") ])
            use! bResp = bClient.PostAsync($"/games/{gameId}", bMove)
            Assert.That(int bResp.StatusCode, Is.EqualTo 303, "opponent's no-JS move must apply")

            // Player A (the fixture client) refreshes the dashboard and sees B's mark.
            use! resp = client.GetAsync("/")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain $"game-{gameId}", "the moved game must be on the dashboard")
            Assert.That(body, Does.Contain "\"player\">X", "A's fresh GET / must show the opponent's placed X")
        }

    // ── No-JS delete via the POST alias (HTML forms can't emit DELETE) ─────────
    [<Test>]
    member this.``no-JS POST /games/{id}/delete removes the game (PRG)``() : Task =
        task {
            // Server enforces a 6-game minimum and other fixtures churn the shared count, so
            // pad with an extra game to guarantee we stay above the floor after deleting.
            let! _pad = this.CreateGame()
            let! gameId = this.CreateGame()
            // Become an assigned player via a no-JS move (303).
            use! _moveResp = postForm $"/games/{gameId}" [ "player", "X"; "position", "TopLeft" ]
            use! delResp = postForm $"/games/{gameId}/delete" []
            Assert.That(int delResp.StatusCode, Is.EqualTo 303, "no-JS delete must Post/Redirect/Get")
            Assert.That(delResp.Headers.Location.ToString(), Is.EqualTo "/")

            use! getResp = client.GetAsync($"/games/{gameId}")
            Assert.That(int getResp.StatusCode, Is.EqualTo 404, "deleted game must be gone")
        }
