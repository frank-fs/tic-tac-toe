namespace TicTacToe.Web.Simple.Tests

open System
open System.Threading.Tasks
open NUnit.Framework
open Microsoft.Playwright

/// Base class for Playwright tests against V_simple (port 5328).
/// Manages browser lifecycle and provides a fresh page per test.
[<AbstractClass>]
type TestBase() =
    let mutable playwright: IPlaywright = null
    let mutable browser: IBrowser = null
    let mutable context: IBrowserContext = null
    let mutable page: IPage = null

    let baseUrl =
        Environment.GetEnvironmentVariable("TEST_BASE_URL")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty(s)))
        |> Option.defaultValue "http://localhost:5328"

    let timeoutMs =
        Environment.GetEnvironmentVariable("TEST_TIMEOUT_MS")
        |> Option.ofObj
        |> Option.bind (fun s ->
            match Int32.TryParse(s) with
            | true, v when v > 0 -> Some v
            | _ -> None)
        |> Option.defaultValue 5000

    let mutable additionalContexts: IBrowserContext list = []

    member _.Page = page
    member _.Context = context
    member _.Browser = browser
    member _.BaseUrl = baseUrl
    member _.TimeoutMs = timeoutMs

    /// Creates a new authenticated page (separate user) for multi-player tests.
    member this.CreateSecondPlayer(arenaUrl: string) : Task<IPage> =
        task {
            let! ctx = browser.NewContextAsync()
            additionalContexts <- ctx :: additionalContexts

            let! newPage = ctx.NewPageAsync()

            let options = PageGotoOptions(Timeout = Nullable(float32 timeoutMs))
            let! _ = newPage.GotoAsync($"{baseUrl}/login", options)
            let! _ = newPage.GotoAsync(arenaUrl, options)

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
                // Visit /login to get an auth cookie
                let! _ = page.GotoAsync($"{baseUrl}/login", options)
                // Navigate to home
                let! _ = page.GotoAsync(baseUrl, options)
                ()
            with ex ->
                let message =
                    $"Cannot connect to {baseUrl}. Ensure the server is running: dotnet run --project src/TicTacToe.Web.Simple/"
                raise (Exception(message, ex))
        }

    [<TearDown>]
    member _.TeardownPage() : Task =
        task {
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
