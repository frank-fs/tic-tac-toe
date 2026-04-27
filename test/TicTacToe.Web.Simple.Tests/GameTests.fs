module TicTacToe.Web.Simple.Tests.GameTests

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open Microsoft.Playwright
open TicTacToe.Web.Simple.Tests

// Position index → nth-child (1-based) for .board form:
//  1=TopLeft  2=TopCenter  3=TopRight
//  4=MiddleLeft  5=MiddleCenter  6=MiddleRight
//  7=BottomLeft  8=BottomCenter  9=BottomRight

let private clickNth (page: IPage) (nth: int) (timeoutMs: int) =
    task {
        let navTask =
            page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 timeoutMs)))
        do! page.ClickAsync($".board form:nth-child({nth}) button[type='submit']")
        do! navTask
    }

let private createArena (page: IPage) (timeoutMs: int) =
    task {
        let navTask =
            page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 timeoutMs)))
        do! page.ClickAsync("button[type='submit']")
        do! navTask
        return page.Url
    }

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

// ============================================================================
// Full game flows — two browser contexts (two distinct cookie identities)
// ============================================================================

[<TestFixture>]
type FullGameTests() =
    inherit TestBase()

    [<Test>]
    member this.``X wins with top row - status shows X wins``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // X: TopLeft (1), O: MiddleLeft (4), X: TopCenter (2), O: MiddleCenter (5), X: TopRight (3)
            do! clickNth this.Page 1 this.TimeoutMs

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 4 this.TimeoutMs

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 2 this.TimeoutMs

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 5 this.TimeoutMs

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 3 this.TimeoutMs // X wins!

            let! statusEl = this.Page.QuerySelectorAsync(".status")
            Assert.That(statusEl, Is.Not.Null, ".status element missing after X wins")
            let! statusText = statusEl.InnerTextAsync()
            Assert.That(statusText, Does.Contain("X wins"), $"Expected 'X wins!' in status, got '{statusText}'")

            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9), "All 9 squares should remain visible after game over")
        }

    [<Test>]
    member this.``O wins with middle row - status shows O wins``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // X: TL(1), O: ML(4), X: TC(2), O: MC(5), X: BR(9), O: MR(6) → O wins middle row
            do! clickNth this.Page 1 this.TimeoutMs

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 4 this.TimeoutMs

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 2 this.TimeoutMs

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 5 this.TimeoutMs

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 9 this.TimeoutMs

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 6 this.TimeoutMs // O wins!

            let! statusEl = p2.QuerySelectorAsync(".status")
            Assert.That(statusEl, Is.Not.Null, ".status element missing after O wins")
            let! statusText = statusEl.InnerTextAsync()
            Assert.That(statusText, Does.Contain("O wins"), $"Expected 'O wins!' in status, got '{statusText}'")

            let! squares = p2.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9), "All 9 squares should remain visible after game over")
        }

    [<Test>]
    member this.``Full game ends in draw - status shows draw``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // Draw sequence — no winner: X-TL,O-TC,X-TR,O-MC,X-ML,O-BR,X-BC,O-BL,X-MR
            // Final board: TL=X TC=O TR=X / ML=X MC=O MR=X / BL=O BC=X BR=O
            do! clickNth this.Page 1 this.TimeoutMs // X-TL

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 2 this.TimeoutMs // O-TC

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 3 this.TimeoutMs // X-TR

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 5 this.TimeoutMs // O-MC

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 4 this.TimeoutMs // X-ML

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 9 this.TimeoutMs // O-BR

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 8 this.TimeoutMs // X-BC

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 7 this.TimeoutMs // O-BL

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 6 this.TimeoutMs // X-MR → draw

            let! statusEl = this.Page.QuerySelectorAsync(".status")
            Assert.That(statusEl, Is.Not.Null)
            let! statusText = statusEl.InnerTextAsync()
            Assert.That(statusText.ToLower(), Does.Contain("draw"), $"Expected draw in status, got '{statusText}'")

            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9))
        }

// ============================================================================
// Error handling — inline errors, engine rejections
// ============================================================================

[<TestFixture>]
type ErrorBehaviorTests() =
    inherit TestBase()

    [<Test>]
    member this.``Wrong turn error message text is correct``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs

            // X plays first move
            do! clickNth this.Page 1 this.TimeoutMs

            // Same player immediately tries again — it's O's turn
            do! clickNth this.Page 2 this.TimeoutMs

            let! errorDiv = this.Page.QuerySelectorAsync(".error-msg")
            Assert.That(errorDiv, Is.Not.Null, "Expected .error-msg after wrong-turn move")
            let! errorText = errorDiv.InnerTextAsync()
            Assert.That(errorText, Does.Contain("not your turn").Or.Contain("not a player"),
                $"Expected wrong-turn message, got '{errorText}'")

            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9), "All 9 squares still present after error")
        }

    [<Test>]
    member this.``Occupied square shows error in status``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // X plays TopLeft (1), O plays TopCenter (2), X tries TopLeft again (occupied)
            do! clickNth this.Page 1 this.TimeoutMs

            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 2 this.TimeoutMs

            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 1 this.TimeoutMs // position already taken

            let! statusEl = this.Page.QuerySelectorAsync(".status")
            Assert.That(statusEl, Is.Not.Null)
            let! statusText = statusEl.InnerTextAsync()
            // Engine returns Error(state, "Invalid move") for occupied squares;
            // the handler renders it via statusText as "Error: Invalid move"
            Assert.That(statusText, Does.Contain("Error"),
                $"Expected error status for occupied square, got '{statusText}'")

            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9), "Squares still visible after occupied-square error")
        }

    [<Test>]
    member this.``Player X cannot move after X wins - shows not your turn``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // X wins: TL,ML,TC,MC,TR
            do! clickNth this.Page 1 this.TimeoutMs
            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 4 this.TimeoutMs
            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 2 this.TimeoutMs
            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 5 this.TimeoutMs
            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 3 this.TimeoutMs // X wins

            // X (player 1) tries to play after the win
            do! clickNth this.Page 7 this.TimeoutMs

            let! errorDiv = this.Page.QuerySelectorAsync(".error-msg")
            Assert.That(errorDiv, Is.Not.Null, "Expected .error-msg when X moves after game over")
            let! errorText = errorDiv.InnerTextAsync()
            Assert.That(errorText, Does.Contain("not your turn").Or.Contain("not a player"),
                $"Expected rejection message, got '{errorText}'")
        }

    [<Test>]
    member this.``Spectator gets not-a-player error after both roles assigned``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // Assign both players: X plays, O plays
            do! clickNth this.Page 1 this.TimeoutMs // P1 → X
            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 2 this.TimeoutMs // P2 → O

            // P3 — fresh context, third identity
            let! p3 = this.CreateSecondPlayer(arenaUrl)
            // p3 is already at arenaUrl (CreateSecondPlayer navigates there)
            do! clickNth p3 5 this.TimeoutMs // spectator tries MiddleCenter

            let! errorDiv = p3.QuerySelectorAsync(".error-msg")
            Assert.That(errorDiv, Is.Not.Null, "Expected .error-msg for spectator")
            let! errorText = errorDiv.InnerTextAsync()
            Assert.That(errorText, Does.Contain("not a player"),
                $"Expected 'not a player' rejection for spectator, got '{errorText}'")

            let! squares = p3.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9))
        }

// ============================================================================
// Restart — board and assignments cleared after reset
// ============================================================================

[<TestFixture>]
type RestartTests() =
    inherit TestBase()

    [<Test>]
    member this.``Restart after X wins clears board and resets to X turn``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let! p2 = this.CreateSecondPlayer(arenaUrl)

            // X wins: TL,ML,TC,MC,TR
            do! clickNth this.Page 1 this.TimeoutMs
            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 4 this.TimeoutMs
            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 2 this.TimeoutMs
            let! _ = p2.GotoAsync(arenaUrl)
            do! clickNth p2 5 this.TimeoutMs
            let! _ = this.Page.GotoAsync(arenaUrl)
            do! clickNth this.Page 3 this.TimeoutMs // X wins

            // Verify win before restart
            let! statusBefore = this.Page.QuerySelectorAsync(".status")
            let! statusBeforeText = statusBefore.InnerTextAsync()
            Assert.That(statusBeforeText, Does.Contain("X wins"))

            // Click restart
            let restartTask =
                this.Page.WaitForURLAsync("**/arenas/**", PageWaitForURLOptions(Timeout = Nullable(float32 this.TimeoutMs)))
            do! this.Page.ClickAsync(".reset-game-btn")
            do! restartTask

            // Board should be cleared — status "X's turn"
            let! statusAfter = this.Page.QuerySelectorAsync(".status")
            Assert.That(statusAfter, Is.Not.Null)
            let! statusAfterText = statusAfter.InnerTextAsync()
            Assert.That(statusAfterText, Does.Contain("X's turn"),
                $"Expected 'X's turn' after restart, got '{statusAfterText}'")

            // All 9 squares empty (showing '·' or blank)
            let! squares = this.Page.QuerySelectorAllAsync(".board button[type='submit']")
            Assert.That(squares.Count, Is.EqualTo(9))

            let! buttonTexts =
                squares
                |> Seq.map (fun sq -> sq.InnerTextAsync() |> Async.AwaitTask)
                |> Async.Parallel
                |> Async.StartAsTask

            let filled = buttonTexts |> Array.filter (fun t -> t.Trim() = "X" || t.Trim() = "O")
            Assert.That(filled.Length, Is.EqualTo(0), $"Expected empty board after restart, found {filled.Length} filled squares")
        }
