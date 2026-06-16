namespace TicTacToe.Web.Tests

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open NUnit.Framework

/// Phase A E2E for the "Proto progressive enhancement" milestone (spec:
/// docs/superpowers/specs/2026-06-15-proto-same-url-conneg-pe-design.md).
///
/// Scope (revised): the research-critical no-JS html path — server-rendered discovery on
/// GET /, refresh-to-update freshness, and a11y. Live updates after initial load keep using
/// the dedicated /sse stream (same-URL event-stream negotiation was dropped: a landing page
/// serving an open-until-close stream from its own URL is the wrong shape).
///
/// Raw HttpClient (NOT Playwright) on purpose: Playwright executes the Datastar JS, so it
/// cannot prove the page is usable with no JS.
[<TestFixture>]
type ProtoPeServerRenderTests() =
    let mutable client: HttpClient = null
    let mutable handler: HttpClientHandler = null

    let baseUrl =
        Environment.GetEnvironmentVariable("TEST_BASE_URL")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty(s)))
        |> Option.defaultValue "http://localhost:5000"

    let getHtml (url: string) (ct: CancellationToken) : Task<HttpResponseMessage> =
        let req = new HttpRequestMessage(HttpMethod.Get, url)
        req.Headers.Accept.Clear()
        req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("text/html"))
        client.SendAsync(req, ct)

    [<OneTimeSetUp>]
    member _.Setup() : Task =
        task {
            handler <- new HttpClientHandler(CookieContainer = Net.CookieContainer(), AllowAutoRedirect = true)
            client <- new HttpClient(handler, BaseAddress = Uri(baseUrl))
            // /login issues the auth cookie into the handler's CookieContainer (home requires auth).
            let! _ = client.GetAsync("/login")
            ()
        }

    [<OneTimeTearDown>]
    member _.Teardown() =
        if not (isNull client) then client.Dispose(); client <- null
        if not (isNull handler) then handler.Dispose(); handler <- null

    member private _.CreateGame() : Task<string> =
        task {
            let! resp = client.PostAsync("/games", null)
            let loc = resp.Headers.Location.ToString()
            return loc.Substring("/games/".Length)
        }

    // ── E2E #1 — no-JS discovery on GET / ───────────────────────────────────────
    // Falsifiable: the body can only contain a game board + its position form if the
    // server enumerated active games AT RENDER TIME. Today GET / was an empty shell whose
    // games arrived only over SSE, so a no-JS GET saw "Connecting..." and zero boards.
    [<Test>]
    member this.``GET / server-renders all active games with position forms``() : Task =
        task {
            let! gameId = this.CreateGame()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            use! resp = getHtml "/" cts.Token
            Assert.That(int resp.StatusCode, Is.EqualTo 200)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "game-board", "no-JS GET / must contain game boards")
            Assert.That(body, Does.Contain $"game-{gameId}", "must contain the created game's board")
            Assert.That(body, Does.Contain "action=\"/games/", "boards must carry playable position forms (no-JS POST)")
            Assert.That(body, Does.Not.Contain "Connecting...", "must not be the empty SSE-loading shell")
        }

    // ── E2E #2 — html representation of a game ──────────────────────────────────
    [<Test>]
    member this.``GET /games/{id} server-renders the board``() : Task =
        task {
            let! gameId = this.CreateGame()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            use! resp = getHtml $"/games/{gameId}" cts.Token
            Assert.That(int resp.StatusCode, Is.EqualTo 200)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "game-board", "the game's html must be the server-rendered board")
        }

    // ── E2E #5 — no-JS freshness: refresh re-fetches current state ──────────────
    // Falsifiable: a move made by POST must be visible on a FRESH GET / with no JS/SSE.
    // This is the re-fetch-by-handle freshness the L3 study showed agents sustain.
    [<Test>]
    member this.``GET / after a move shows the new mark (refresh freshness)``() : Task =
        task {
            let! gameId = this.CreateGame()
            let signals = sprintf """{"gameId":"%s","player":"X","position":"TopLeft"}""" gameId
            let content = new StringContent(signals, Text.Encoding.UTF8, "application/json")
            content.Headers.ContentType.MediaType <- "application/json"
            use moveReq = new HttpRequestMessage(HttpMethod.Post, $"/games/{gameId}", Content = content)
            moveReq.Headers.Add("datastar-request", "true")
            let! _ = client.SendAsync(moveReq)
            do! Task.Delay 200

            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            use! resp = getHtml "/" cts.Token
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain $"game-{gameId}", "the moved game must appear on /")
            Assert.That(body, Does.Contain "\"player\">X", "a fresh GET / must show the placed X mark")
        }

    // ── E2E #6 — a11y attributes on rendered boards ─────────────────────────────
    // aria-label (position-labeled controls) already existed; aria-live is added by #61.
    [<Test>]
    member this.``rendered board carries aria-live and position-labeled controls``() : Task =
        task {
            let! gameId = this.CreateGame()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            use! resp = getHtml $"/games/{gameId}" cts.Token
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "aria-label", "position-labeled controls must be present")
            Assert.That(body, Does.Contain "aria-live", "boards must carry an aria-live region for SR announcements")
        }
