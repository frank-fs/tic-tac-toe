module TicTacToe.Web.templates.home

open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine
open TicTacToe.Web.templates.game

let homePage (ctx: HttpContext) (allowCreate: bool) (gameBoards: HtmlElement seq) =
    ctx.Items["Title"] <- "Tic Tac Toe"

    Fragment() {
        // Include game styles
        gameStyles

        div(class' = "game-container") {
            h1(class' = "title") { "Tic Tac Toe" }

            // New Game button - creates a game via POST /games.
            // Withheld once the game cap is reached.
            if allowCreate then
                div(class' = "new-game-container") {
                    button(class' = "new-game-btn", type' = "button")
                        .attr("data-on:click", "@post('/games')") {
                        "New Game"
                    }
                }
            else
                Fragment() { }

            // Games container - server-rendered so the dashboard is discoverable and
            // playable with no JS; the JS path's SSE stream morphs these boards in place.
            div(id = "games-container", class' = "games-container") {
                for board in gameBoards do board
            }

            div(class' = "game-info") {
                p() { "Play locally - X and O take turns" }
            }
        }
    }
