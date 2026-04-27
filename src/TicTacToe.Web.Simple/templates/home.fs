module TicTacToe.Web.Simple.templates.home

open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Simple.templates.game

#nowarn "3391"

let private arenaStatusText = function
    | XTurn _ -> "X's turn"
    | OTurn _ -> "O's turn"
    | Won(_, player) -> $"{player} wins!"
    | Draw _ -> "Draw"
    | Error(_, msg) -> $"Error: {msg}"

let homePage (ctx: HttpContext) (arenas: (string * MoveResult) list) =
    ctx.Items["Title"] <- "Tic Tac Toe — Arenas"

    Fragment() {
        homeStyles

        h1 (class' = "title") { "Tic Tac Toe" }

        div (class' = "new-arena-container") {
            form (method = "post", action = "/arenas") {
                button (class' = "new-arena-btn", type' = "submit") { "New Arena" }
            }
        }

        if arenas.IsEmpty then
            p (class' = "no-arenas") { "No arenas yet. Create one to start playing!" }
        else
            ul (class' = "arenas-list") {
                for (arenaId, result) in arenas do
                    li (class' = "arena-item") {
                        a (class' = "arena-link", href = $"/arenas/{arenaId}") {
                            arenaId.[..7]
                        }
                        span (class' = "arena-status") { arenaStatusText result }
                    }
            }
    }
