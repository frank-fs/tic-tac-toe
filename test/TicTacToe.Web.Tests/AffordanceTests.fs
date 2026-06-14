namespace TicTacToe.Web.Tests

open System.Threading.Tasks
open NUnit.Framework
open Microsoft.Playwright

/// Tests for role-specific affordances - verify that each user sees only controls appropriate to their role
[<TestFixture>]
type AffordanceTests() =
    inherit TestBase()

    /// T011: Player X on X's turn sees clickable squares (can make move) and reset/delete buttons
    [<Test>]
    member this.``Player X on their turn sees clickable move squares`` () : Task =
        task {
            // Create a dedicated new game for this test to avoid shared state
            do! this.Page.ClickAsync("button:has-text('New Game')")
            let! _ = this.Page.WaitForTimeoutAsync(200.0f) // Wait for game creation

            // Get the newly created game's ID (should be the last one)
            let! allGames = this.Page.Locator("[id^=game-]").AllAsync()
            let! id = allGames.[allGames.Count - 1].GetAttributeAsync("id")

            // Verify that Player X sees clickable squares (square elements with class square-clickable)
            let clickableSquares = this.Page.Locator($"#{id} .square-clickable")
            let! count = clickableSquares.CountAsync()
            Assert.That(count, Is.GreaterThan(0), "Player X should see clickable squares on their turn")

            // Verify reset button is visible and enabled
            let resetBtn = this.Page.Locator($"#{id} button:has-text('Reset')")
            let! isVisible = resetBtn.IsVisibleAsync()
            Assert.That(isVisible, Is.True, "Reset button should be visible for assigned player")

            // Verify delete button is visible and enabled
            let deleteBtn = this.Page.Locator($"#{id} button:has-text('Delete')")
            let! isVisible = deleteBtn.IsVisibleAsync()
            Assert.That(isVisible, Is.True, "Delete button should be visible for assigned player")
        }

    /// T012: Player X after moving sees reset/delete but NO clickable move squares (opponent's turn)
    [<Test>]
    member this.``Player X on opponent's turn sees reset/delete but no move squares`` () : Task =
        task {
            // Create two browser contexts for two-player game
            let! player2Page = this.CreateSecondPlayer(this.BaseUrl)

            // Player 1 creates a dedicated new game for this test
            do! this.Page.ClickAsync("button:has-text('New Game')")
            let! _ = this.Page.WaitForTimeoutAsync(200.0f) // Wait for game creation

            // Get the newly created game's ID (should be the last one)
            let! allGames = this.Page.Locator("[id^=game-]").AllAsync()
            let! id = allGames.[allGames.Count - 1].GetAttributeAsync("id")

            // Player 1 makes a move (claims X). The move is a form-submit button
            // (progressive enhancement); datastar enhances the submit.
            let firstSquare = this.Page.Locator($"#{id} .square-clickable").First
            do! firstSquare.ClickAsync()

            // Wait for move to be processed
            do! this.Page.WaitForTimeoutAsync(500.0f)

            // Verify Player 1 now sees NO clickable squares (it's O's turn)
            let clickableSquares = this.Page.Locator($"#{id} .square-clickable")
            let! count = clickableSquares.CountAsync()
            Assert.That(count, Is.EqualTo(0), "Player X should NOT see clickable squares on opponent's turn")

            // Verify Player 1 still sees enabled reset/delete buttons
            let resetBtn = this.Page.Locator($"#{id} .reset-game-btn:not(:disabled)")
            let! resetCount = resetBtn.CountAsync()
            Assert.That(resetCount, Is.GreaterThan(0), "Reset button should be enabled when waiting for opponent")

            let deleteBtn = this.Page.Locator($"#{id} .delete-game-btn:not(:disabled)")
            let! deleteCount = deleteBtn.CountAsync()
            Assert.That(deleteCount, Is.GreaterThan(0), "Delete button should be enabled when waiting for opponent")
        }
