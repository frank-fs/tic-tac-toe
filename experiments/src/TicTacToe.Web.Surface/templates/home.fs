module TicTacToe.Web.Surface.templates.home

open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Surface.Surface
open TicTacToe.Web.Surface.templates.game

#nowarn "3391"

let private arenaStatusText = function
    | XTurn _ -> "X's turn"
    | OTurn _ -> "O's turn"
    | Won(_, player) -> $"{player} wins!"
    | Draw _ -> "Draw"
    | Error(_, msg) -> $"Error: {msg}"

let homePage (surface: Surface) (ctx: HttpContext) (allowCreate: bool) (arenas: (string * MoveResult) list) =
    ctx.Items["Title"] <- "Tic Tac Toe — Games"

    Fragment() {
        homeStyles

        h1 (class' = "title") { "Tic Tac Toe" }

        // Creation affordance is withheld once the game cap is reached.
        if allowCreate then
            div (class' = "new-arena-container") {
                form (method = "post", action = "/arenas") {
                    button (class' = "new-arena-btn", type' = "submit") { "New Game" }
                }
            }
        else
            Fragment() { }

        if arenas.IsEmpty then
            p (class' = "no-arenas") { "No games yet. Create one to start playing!" }
        else
            let arenaList =
                let u = ul (class' = "arenas-list") {
                    for (arenaId, result) in arenas do
                        li (class' = "arena-item") {
                            a (class' = "arena-link", href = $"/arenas/{arenaId}") {
                                arenaId.[..7]
                            }
                            span (class' = "arena-status") { arenaStatusText result }
                        }
                }
                if surface.C then u.attr("role", "list") else u
            arenaList
    }
