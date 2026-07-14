namespace TicTacToe.Web.Tests

open System
open NUnit.Framework

/// The Playwright suite drives a LIVE app. TEST_BASE_URL, when set, points at a server the caller
/// already runs (CI, a manual `dotnet run`). When it is not set, this global fixture boots one on a
/// free port for the whole run and tears it down after — so `dotnet test test/TicTacToe.Web.Tests/`
/// is self-contained rather than silently failing against nothing.
[<SetUpFixture>]
type SharedServer() =

    static let mutable owned: ConfiguredServer option = None
    static let mutable baseUrl = "http://localhost:5000"

    /// The URL every Playwright test navigates to.
    static member BaseUrl = baseUrl

    [<OneTimeSetUp>]
    member _.StartServer() =
        match Environment.GetEnvironmentVariable "TEST_BASE_URL" with
        | null | "" ->
            // App defaults: 6 seeded games, no cap — the same server test/CLAUDE.md documents.
            let server = new ConfiguredServer(6)
            owned <- Some server
            baseUrl <- server.BaseUrl
        | url -> baseUrl <- url

    [<OneTimeTearDown>]
    member _.StopServer() =
        owned |> Option.iter (fun s -> (s :> IDisposable).Dispose())
        owned <- None
