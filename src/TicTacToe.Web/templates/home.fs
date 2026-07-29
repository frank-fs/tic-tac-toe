module TicTacToe.Web.templates.home

open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine
open TicTacToe.Web.Surface
open TicTacToe.Web.templates.game

/// The create-game form. Two of these can appear on the page (toolbar + the open table at the
/// end of the grid); both are the SAME affordance -- same method, action, rel and datastar
/// expression -- so an agent reading `rel="create-game"` sees one kind of action, just in two
/// places, the way a real page has a toolbar button and an empty slot that both mean "new".
/// If an affordance COUNT is being measured, pass `allowCreate` to only one of them.
let private createGameForm (formClass: string) (inner: HtmlElement) =
    form(class' = formClass, method = "post", action = "/games")
        .attr("rel", "create-game")
        .attr("data-on:submit__prevent", "@post('/games')") {
        inner
    }

let homePage (surface: Surface) (ctx: HttpContext) (allowCreate: bool) (gameBoards: HtmlElement seq) =
    ctx.Items["Title"] <- "Tic Tac Toe"

    // Materialised once: the count is shown in the toolbar and decides the empty state.
    let boards = gameBoards |> Seq.toList

    Fragment() {
        // Include game styles
        gameStyles

        div(class' = "game-container") {
            // Toolbar: what this is, how many tables are out, and how to put another one out.
            // Replaces the centered <h1> + centered button stack, which pushed the first row
            // of boards below the fold once there were more than a few.
            div(class' = "app-bar") {
                div(class' = "app-bar-titles") {
                    h1(class' = "title") { "Tic Tac Toe" }
                    span(class' = "board-count") {
                        if boards.Length = 1 then "1 board" else sprintf "%d boards" boards.Length
                    }
                }

                // New Game button - creates a game via POST /games.
                // Withheld once the game cap is reached.
                if allowCreate then
                    div(class' = "new-game-container") {
                        // Real form so a game can be created with no JS; datastar enhances the
                        // submit when present.
                        createGameForm "" (button(class' = "new-game-btn", type' = "submit") { "New Game" })
                    }
                else
                    Fragment() { }
            }

            // Games container - server-rendered so the dashboard is discoverable and
            // playable with no JS; the JS path's SSE stream morphs these boards in place.
            // Now a uniform grid: fixed 110px cells, auto-filling rows, stable creation order.
            div(id = "games-container", class' = "games-container") {
                for board in boards do board

                // The open table: an empty slot at the END of the grid, the way an unoccupied
                // table reads in a park. Same POST /games affordance as the toolbar button.
                //
                // Pinned last by CSS (`order: 1` on .add-game-form/.add-game-slot), not by DOM
                // position: an SSE morph that appends a new board after this node, or any change
                // to render order, would otherwise leave the open table stranded mid-floor.
                //
                // `allowCreate` is false at the game cap, so at capacity the slot is not
                // rendered at all -- the affordance is withheld exactly like the toolbar button,
                // and a non-interactive note takes its place so the floor doesn't just end
                // silently. (Same withholding rule the at-capacity error banner assumes.)
                if allowCreate then
                    createGameForm "add-game-form" (
                        let b = button(class' = "add-game-slot", type' = "submit") { "+" }
                        // The glyph is not an accessible name, so this label is not gated on C.
                        b.attr("aria-label", "Add a new game board").attr("title", "Add a new game board")
                    )
                elif not (List.isEmpty boards) then
                    div(class' = "at-capacity-slot") { "Game limit reached" }
                else
                    Fragment() { }

                // Zero games: keep the floor plan legible instead of showing a bare page.
                // Decorative only -- the real affordance is the add slot above.
                if List.isEmpty boards then
                    Fragment() {
                        for _ in 1..5 do
                            div(class' = "empty-slot").attr("aria-hidden", "true")
                    }
                else
                    Fragment() { }
            }

            // The one visible copy of the game's explanation. Per-board intros are now
            // visually hidden (see .game-intro in gameStyles), so this line is what a
            // sighted visitor reads once, rather than 15 times.
            div(class' = "game-info") {
                p() { "Play locally - X and O take turns" }
            }
        }
    }
