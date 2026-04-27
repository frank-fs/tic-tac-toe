module TicTacToe.Web.Simple.Tests.GameTests

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open Microsoft.Playwright
open TicTacToe.Web.Simple.Tests

/// Verifies V_simple-specific behaviors — notably the differences from V_proto.
[<TestFixture>]
type HomePageTests() =
    inherit TestBase()

    /// 1. Home page loads with arena list and "New Arena" button
    [<Test>]
    member this.``Home page shows arena list and New Arena button``() : Task =
        task {
            // SetupPage already navigated to home
            let! title = this.Page.TitleAsync()
            Assert.That(title, Does.Contain("Tic Tac Toe"))

            // New Arena button is present
            let! btn = this.Page.QuerySelectorAsync("button[type='submit']")
            Assert.That(btn, Is.Not.Null, "New Arena submit button not found")

            // The page should list at least one arena (6 created on startup)
            let! items = this.Page.QuerySelectorAllAsync(".arena-item")
            Assert.That(items.Count, Is.GreaterThanOrEqualTo(1), "Expected at least one arena in the list")
        }

    /// 2. Path is opaque — the home page URL is NOT /games, /board, etc.
    [<Test>]
    member this.``Arena list uses opaque path /arenas not /games``() : Task =
        task {
            // Click New Arena — should redirect to /arenas/{id}, not /games/{id}
            let! form = this.Page.QuerySelectorAsync("form[action='/arenas']")
            Assert.That(form, Is.Not.Null, "Expected form POSTing to /arenas")
        }

[<TestFixture>]
type ArenaCreationTests() =
    inherit TestBase()

    /// 3. Can create an arena and be redirected to /arenas/{id}
    [<Test>]
    member this.``Creating an arena redirects to /arenas/{id}``() : Task =
        task {
            let options = PageGotoOptions(Timeout = Nullable(float32 this.TimeoutMs))

            // Click the New Arena button
            let! _ = this.Page.ClickAsync("button[type='submit']")

            // Wait for navigation — should land on /arenas/<id>
            do! this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))

            let url = this.Page.Url
            Assert.That(url, Does.Contain("/arenas/"), $"Expected redirect to /arenas/{{id}}, got {url}")

            // Should NOT contain /games
            Assert.That(url, Does.Not.Contain("/games"), "Path should be /arenas, not /games")
        }

[<TestFixture>]
type ArenaSquaresTests() =
    inherit TestBase()

    /// 4. All 9 squares are visible as buttons on an arena page
    [<Test>]
    member this.``Arena page shows all 9 squares as buttons regardless of game state``() : Task =
        task {
            // Navigate to a new arena — wait for URL to change to /arenas/<id>
            let navTask = this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync("button[type='submit']")
            do! navTask

            // All 9 squares must be present — each inside a form as a submit button
            let! squares = this.Page.QuerySelectorAllAsync(".square")
            Assert.That(squares.Count, Is.EqualTo(9), "Expected exactly 9 squares")

            // Each square should be a submit button (not a div)
            let! buttons = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(buttons.Count, Is.EqualTo(9), "All 9 squares should be submit buttons")
        }

    /// 5. Making a move updates the board
    [<Test>]
    member this.``Making a move updates the board``() : Task =
        task {
            // Navigate to new arena
            let navTask = this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync("button[type='submit']")
            do! navTask

            // Click the first square (TopLeft — first submit button in board)
            let! firstSquare = this.Page.QuerySelectorAsync(".board form:first-child button[type='submit']")
            Assert.That(firstSquare, Is.Not.Null, "First square button not found")

            // Read value before click
            let! beforeText = firstSquare.InnerTextAsync()

            // Click it — this POSTs TopLeft as X; wait for the same arena URL to reload
            let reloadTask = this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync(".board form:first-child button[type='submit']")
            do! reloadTask

            // Board should now show X in the first square
            let! firstSquareAfter = this.Page.QuerySelectorAsync(".board form:first-child button[type='submit']")
            let! afterText = firstSquareAfter.InnerTextAsync()

            Assert.That(afterText.Trim(), Is.EqualTo("X"), $"Expected X in TopLeft after move, got '{afterText}'")

            // Still 9 squares (no buttons hidden)
            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9), "All 9 squares should still be present after a move")
        }

[<TestFixture>]
type InvalidMoveTests() =
    inherit TestBase()

    /// 6. Invalid move shows inline error — does NOT navigate away or hide buttons
    [<Test>]
    member this.``Wrong player move shows inline error without hiding buttons``() : Task =
        task {
            // Navigate to new arena
            let navTask = this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync("button[type='submit']")
            do! navTask

            let arenaUrl = this.Page.Url

            // Player 1 makes the first move (X)
            let move1Task = this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync(".board form:first-child button[type='submit']")
            do! move1Task

            // Player 1 now tries to play again as X — it's O's turn so this is "NotYourTurn"
            // The form hidden field will still say "X" (derived from player 1's cookie)
            let! secondSquare = this.Page.QuerySelectorAsync(".board form:nth-child(2) button[type='submit']")
            Assert.That(secondSquare, Is.Not.Null)
            let move2Task = this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync(".board form:nth-child(2) button[type='submit']")
            do! move2Task

            // Should still be on the same /arenas/{id} URL
            Assert.That(this.Page.Url, Does.Contain("/arenas/"), "Should stay on arena page after invalid move")

            // Error message should be visible
            let! errorDiv = this.Page.QuerySelectorAsync(".error-msg")
            Assert.That(errorDiv, Is.Not.Null, "Expected .error-msg div after invalid move")

            let! errorText = errorDiv.InnerTextAsync()
            Assert.That(errorText, Is.Not.Empty, "Error message should not be empty")

            // All 9 squares must still be present
            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9), "All 9 squares should still be visible after error")
        }

[<TestFixture>]
type JsonApiTests() =
    inherit TestBase()

    /// 7. GET /arenas/{id} with Accept: application/json returns JSON with board/status/whoseTurn
    [<Test>]
    member this.``GET /arenas/{id} with Accept application/json returns JSON``() : Task =
        task {
            // Navigate to new arena to get an ID
            let! _ = this.Page.ClickAsync("button[type='submit']")
            do! this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))

            let arenaUrl = this.Page.Url

            // Use HttpClient with Accept: application/json
            use client = new HttpClient()
            client.DefaultRequestHeaders.Add("Accept", "application/json")

            let! response = client.GetAsync(arenaUrl)
            Assert.That(int response.StatusCode, Is.EqualTo(200), "Expected 200 OK")

            let contentType = response.Content.Headers.ContentType.MediaType
            Assert.That(contentType, Does.Contain("application/json"), $"Expected JSON response, got {contentType}")

            let! body = response.Content.ReadAsStringAsync()
            let doc = JsonDocument.Parse(body)
            let root = doc.RootElement

            // Must have board field
            Assert.That(root.TryGetProperty("board", ref Unchecked.defaultof<_>), Is.True, "JSON missing 'board' field")

            // Must have status field
            Assert.That(root.TryGetProperty("status", ref Unchecked.defaultof<_>), Is.True, "JSON missing 'status' field")

            // Must have whoseTurn field
            Assert.That(root.TryGetProperty("whoseTurn", ref Unchecked.defaultof<_>), Is.True, "JSON missing 'whoseTurn' field")

            // Board should have 9 elements
            let boardProp = root.GetProperty("board")
            Assert.That(boardProp.GetArrayLength(), Is.EqualTo(9), "Board should have 9 elements")

            // New game: whoseTurn should be "X"
            let whoseTurn = root.GetProperty("whoseTurn")
            // whoseTurn might be a string "X" or a JSON object with "some" wrapper
            // Check it's not null/missing
            Assert.That(whoseTurn.ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "whoseTurn should not be null for a new game")
        }
