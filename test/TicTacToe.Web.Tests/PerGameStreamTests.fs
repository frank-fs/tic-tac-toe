namespace TicTacToe.Web.Tests

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Playwright
open NUnit.Framework

/// Base fixture: launches a TicTacToe.Web server with the given game config plus a
/// Playwright browser and an authenticated HttpClient, all torn down at the end.
[<AbstractClass>]
type ConfiguredServerFixture(initialGames: int, maxGames: int) =
    let mutable server : ConfiguredServer = Unchecked.defaultof<ConfiguredServer>
    let mutable playwright : IPlaywright = null
    let mutable browser : IBrowser = null
    let mutable handler : HttpClientHandler = null
    let mutable client : HttpClient = null

    member _.BaseUrl = server.BaseUrl
    member _.Client = client

    [<OneTimeSetUp>]
    member _.OneTimeSetUp() : Task =
        task {
            server <- new ConfiguredServer(initialGames, maxGames)
            let! pw = Playwright.CreateAsync()
            playwright <- pw
            let! b = pw.Chromium.LaunchAsync()
            browser <- b
            handler <- new HttpClientHandler(CookieContainer = Net.CookieContainer(), AllowAutoRedirect = false)
            client <- new HttpClient(handler, BaseAddress = Uri(server.BaseUrl))
            let! _ = client.GetAsync("/login")
            ()
        }

    [<OneTimeTearDown>]
    member _.OneTimeTearDown() =
        if not (isNull client) then client.Dispose()
        if not (isNull handler) then handler.Dispose()
        if not (isNull browser) then browser.CloseAsync().GetAwaiter().GetResult()
        if not (isNull playwright) then playwright.Dispose()
        if not (obj.ReferenceEquals(server, null)) then (server :> IDisposable).Dispose()

    /// A fresh authenticated browser page in its own context (own cookie/identity).
    member this.NewPlayerPage() : Task<IPage> =
        task {
            let! ctx = browser.NewContextAsync()
            let! page = ctx.NewPageAsync()
            let! _ = page.GotoAsync($"{this.BaseUrl}/login")
            return page
        }


/// Experiment config: 1 pre-created game, creation capped at 1 — the run conditions all
/// three arms (ERPC, Simple, Proto) share.
[<TestFixture>]
type ExperimentConfigStreamTests() =
    inherit ConfiguredServerFixture(1, 1)

    /// A move on the single game updates the board live (morph in place): the placed mark
    /// appears and there is still exactly one board element (no duplicate, no clear).
    [<Test>]
    member this.``move on the single game morphs the board live in place``() : Task =
        task {
            let! page = this.NewPlayerPage()
            let! _ = page.GotoAsync($"{this.BaseUrl}/")
            do! TestHelpers.waitForVisible page ".game-board" 10000
            let! boardsBefore = page.Locator(".game-board").CountAsync()
            Assert.That(boardsBefore, Is.EqualTo 1, "experiment config starts with exactly one game")
            do! page.Locator(".square-clickable").First.ClickAsync()
            do! TestHelpers.waitForVisible page ".game-board .player" 10000
            let! boardsAfter = page.Locator(".game-board").CountAsync()
            Assert.That(boardsAfter, Is.EqualTo 1, "move must morph in place — still one board, no duplicate")
            let! marks = page.Locator(".game-board .player").CountAsync()
            Assert.That(marks, Is.GreaterThanOrEqualTo 1, "the placed mark must be visible after the live update")
        }

    /// Under MaxGames=1, a second create is rejected (uniform interface).
    [<Test>]
    member this.``second game creation is rejected at the cap``() : Task =
        task {
            use! resp = this.Client.PostAsync("/games", null)
            Assert.That(int resp.StatusCode, Is.EqualTo 409, "creating past MaxGames=1 must be rejected with 409")
            let! body = resp.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain "MaxGamesReached", "rejection body names the cap")
        }

    /// Per-game connect-resync: opening /games/{id}/sse after a move yields that board's
    /// current state as the first payload.
    [<Test>]
    member this.``per-game stream first payload contains the current board``() : Task =
        task {
            let! home = this.Client.GetAsync("/")
            let! html = home.Content.ReadAsStringAsync()
            let marker = "id=\"game-"
            let idx = html.IndexOf(marker)
            Assert.That(idx, Is.GreaterThanOrEqualTo 0, "dashboard must contain the pre-created game board")
            let start = idx + marker.Length
            let gameId = html.Substring(start, html.IndexOf("\"", start) - start)
            use form = new FormUrlEncodedContent([ Collections.Generic.KeyValuePair("player","X"); Collections.Generic.KeyValuePair("position","TopLeft") ])
            let! _ = this.Client.PostAsync($"/games/{gameId}", form)
            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{gameId}/sse")
            req.Headers.TryAddWithoutValidation("datastar-request", "true") |> ignore
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            use! resp = this.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            use! stream = resp.Content.ReadAsStreamAsync(cts.Token)
            use reader = new StreamReader(stream)
            let sb = System.Text.StringBuilder()
            let buf = Array.zeroCreate<char> 1024
            let mutable keepReading = true
            while keepReading && not (sb.ToString().Contains $"game-{gameId}") do
                let! n = reader.ReadAsync(buf.AsMemory(0, buf.Length)).AsTask()
                if n <= 0 then keepReading <- false else sb.Append(buf, 0, n) |> ignore
            Assert.That(sb.ToString(), Does.Contain $"game-{gameId}", "first per-game SSE frames must carry the current board")
        }


/// Multi-game (dev dashboard) config: several games, creation allowed.
[<TestFixture>]
type MultiGameConfigStreamTests() =
    inherit ConfiguredServerFixture(6, 50)

    /// Creating a game on the connected dashboard appends it live (board count grows by one)
    /// and it is a single element (no duplicate).
    [<Test>]
    member this.``new game appended live to the dashboard as a single board``() : Task =
        task {
            let! page = this.NewPlayerPage()
            let! _ = page.GotoAsync($"{this.BaseUrl}/")
            do! TestHelpers.waitForVisible page ".new-game-btn" 10000
            let! before = page.Locator(".game-board").CountAsync()
            do! page.Locator(".new-game-btn").ClickAsync()
            do! TestHelpers.waitForCount page ".game-board" (before + 1) 10000
            let! after = page.Locator(".game-board").CountAsync()
            Assert.That(after, Is.EqualTo(before + 1), "exactly one new board appended — no duplicate")
        }
