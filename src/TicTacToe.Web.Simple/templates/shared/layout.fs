namespace TicTacToe.Web.Simple.templates.shared

open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine
open TicTacToe.Web.Simple

#nowarn "3391"

module layout =
    let mainLayout (ctx: HttpContext) (content: HtmlElement) =
        let userIdOpt =
            if not (isNull ctx.User) then
                ctx.User.TryGetUserId()
            else
                None

        Fragment() {
            match userIdOpt with
            | Some userId ->
                header (class' = "page-header") {
                    span (class' = "user-identity") { userId.[..7] }
                }
            | None -> ()

            main () { content }
        }

    let html (ctx: HttpContext) (content: HtmlElement) =
        html (lang = "en") {
            head () {
                title () {
                    match ctx.Items.TryGetValue "Title" with
                    | true, title -> string title
                    | false, _ -> "Tic Tac Toe"
                }

                meta (charset = "utf-8")
                meta (name = "viewport", content = "width=device-width, initial-scale=1.0")
                base' (href = "/")
                link (rel = "icon", type' = "image/png", href = "/favicon.png")
            }

            body () { mainLayout ctx content }
        }
