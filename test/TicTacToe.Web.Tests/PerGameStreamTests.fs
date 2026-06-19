namespace TicTacToe.Web.Tests

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open NUnit.Framework

/// E2E for #62 per-game streams. Connect-resync uses raw HttpClient (read the first SSE
/// frame); append/morph uses Playwright (the JS path).
[<TestFixture>]
type PerGameStreamTests() =
    let mutable client: HttpClient = null
    let mutable handler: HttpClientHandler = null

    let baseUrl =
        Environment.GetEnvironmentVariable("TEST_BASE_URL")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty(s)))
        |> Option.defaultValue "http://localhost:5000"

    [<OneTimeSetUp>]
    member _.Setup() : Task =
        task {
            handler <- new HttpClientHandler(CookieContainer = Net.CookieContainer(), AllowAutoRedirect = false)
            client <- new HttpClient(handler, BaseAddress = Uri(baseUrl))
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

    /// AC3: opening /games/{id}/sse after a move yields that board's current state first.
    [<Test>]
    member this.``per-game stream first payload contains the current board``() : Task =
        task {
            let! gameId = this.CreateGame()
            // Make a move so the board is non-empty (so resync content is observable).
            use form = new FormUrlEncodedContent([ Collections.Generic.KeyValuePair("player","X"); Collections.Generic.KeyValuePair("position","TopLeft") ])
            let! _ = client.PostAsync($"/games/{gameId}", form)

            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{gameId}/sse")
            req.Headers.TryAddWithoutValidation("datastar-request", "true") |> ignore
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            use! stream = resp.Content.ReadAsStreamAsync(cts.Token)
            use reader = new StreamReader(stream)
            // Read the first ~2KB of the stream (the connect-resync frame).
            let buf = Array.zeroCreate<char> 2048
            let! n = reader.ReadAsync(buf.AsMemory(0, buf.Length)).AsTask()
            let payload = String(buf, 0, n)
            Assert.That(payload, Does.Contain $"game-{gameId}", "first SSE frame must carry this game's board")
        }

    /// AC2 + AC4: a game created while a dashboard stream is open is appended live, and a
    /// move morphs the board in place (exactly one board element, no duplicate).
    [<Test>]
    member this.``dashboard appends a new game live and keeps one board element``() : Task =
        task {
            let! playwright = Microsoft.Playwright.Playwright.CreateAsync()
            let! browser = playwright.Chromium.LaunchAsync()
            let! page = browser.NewPageAsync()
            let! _ = page.GotoAsync($"{baseUrl}/login")
            let! _ = page.GotoAsync($"{baseUrl}/")
            // Create a game via the API on the shared cookie client; the open dashboard must gain it.
            let! gameId = this.CreateGame()
            do! TestHelpers.waitForCount page $"#game-{gameId}" 1 5000
            // A move morphs in place — still exactly one board element for this game.
            use form = new FormUrlEncodedContent([ Collections.Generic.KeyValuePair("player","X"); Collections.Generic.KeyValuePair("position","TopLeft") ])
            let! _ = client.PostAsync($"/games/{gameId}", form)
            do! TestHelpers.waitForCount page $"#game-{gameId}" 1 5000
            do! browser.CloseAsync()
        }
