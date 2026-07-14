namespace TicTacToe.Web.Tests

open System
open System.Threading.Tasks
open NUnit.Framework
open Microsoft.Playwright

/// Base class for Playwright tests.
/// Manages browser lifecycle and provides fresh page per test.
[<AbstractClass>]
type TestBase() =
    let mutable playwright: IPlaywright = null
    let mutable browser: IBrowser = null
    let mutable context: IBrowserContext = null
    let mutable page: IPage = null

    // Set by the SharedServer global fixture: TEST_BASE_URL when supplied, else the URL of the
    // server it booted for this run.
    let baseUrl = SharedServer.BaseUrl

    let timeoutMs =
        Environment.GetEnvironmentVariable("TEST_TIMEOUT_MS")
        |> Option.ofObj
        |> Option.bind (fun s ->
            match Int32.TryParse(s) with
            | true, v when v > 0 -> Some v
            | _ -> None)
        |> Option.defaultValue 5000

    // Track additional contexts for cleanup
    let mutable additionalContexts: IBrowserContext list = []

    member _.Page = page
    member _.Context = context
    member _.Browser = browser
    member _.BaseUrl = baseUrl
    member _.TimeoutMs = timeoutMs

    /// Creates a new authenticated page (separate user) for multi-player tests.
    /// The page navigates to /login to get its own auth cookie, then to the specified URL.
    member this.CreateSecondPlayer(gameUrl: string) : Task<IPage> =
        task {
            // Create new context (separate cookie jar = separate user)
            let! ctx = browser.NewContextAsync()
            additionalContexts <- ctx :: additionalContexts

            let! newPage = ctx.NewPageAsync()

            // Authenticate by visiting /login
            let options = PageGotoOptions(Timeout = Nullable(float32 timeoutMs))
            let! _ = newPage.GotoAsync($"{baseUrl}/login", options)

            // Navigate to the game
            let! _ = newPage.GotoAsync(gameUrl, options)

            return newPage
        }

    [<OneTimeSetUp>]
    member _.SetupBrowser() : Task =
        task {
            TestContext.WriteLine($"Base URL: {baseUrl}")
            TestContext.WriteLine($"Timeout: {timeoutMs}ms")

            let! pw = Playwright.CreateAsync()
            playwright <- pw

            let headed =
                Environment.GetEnvironmentVariable("HEADED")
                |> Option.ofObj
                |> Option.map (fun s -> s = "1" || s.ToLower() = "true")
                |> Option.defaultValue false

            let! b = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = not headed))
            browser <- b
        }

    [<SetUp>]
    member _.SetupPage() : Task =
        task {
            let! ctx = browser.NewContextAsync()
            context <- ctx

            let! p = ctx.NewPageAsync()
            page <- p

            try
                let options = PageGotoOptions(Timeout = Nullable(float32 timeoutMs))
                // First visit /login to get an auth cookie (home requires auth)
                let! _ = page.GotoAsync($"{baseUrl}/login", options)
                // Now navigate to home - should work with auth cookie
                let! _ = page.GotoAsync(baseUrl, options)
                ()
            with ex ->
                let message = $"Cannot connect to {baseUrl}. Ensure the server is running: dotnet run --project src/TicTacToe.Web/"
                raise (Exception(message, ex))
        }

    [<TearDown>]
    member _.TeardownPage() : Task =
        task {
            // Close additional contexts first
            for ctx in additionalContexts do
                do! ctx.CloseAsync()
            additionalContexts <- []

            if not (isNull page) then
                do! page.CloseAsync()
                page <- null

            if not (isNull context) then
                do! context.CloseAsync()
                context <- null
        }

    [<OneTimeTearDown>]
    member _.TeardownBrowser() =
        if not (isNull browser) then
            browser.CloseAsync() |> Async.AwaitTask |> Async.RunSynchronously
            browser <- null

        if not (isNull playwright) then
            playwright.Dispose()
            playwright <- null
