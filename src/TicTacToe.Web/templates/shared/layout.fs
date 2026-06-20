namespace TicTacToe.Web.templates.shared

open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine
open TicTacToe.Web

#nowarn "3391"

module layout =
    /// Baseline switch: when TICTACTOE_DISABLE_JS=1, omit the datastar bundle and the
    /// SSE auto-connect so the page is pure progressive-enhancement HTML (native form
    /// POST + full reload) — equivalent to a JS-disabled browser, which browsegrab cannot
    /// configure. Read once at module init (immutable).
    let private jsEnabled =
        System.Environment.GetEnvironmentVariable "TICTACTOE_DISABLE_JS" <> "1"

    let mainLayout (ctx: HttpContext) (content: HtmlElement) =
        let userIdOpt =
            if not (isNull ctx.User) then
                ctx.User.TryGetUserId()
            else
                None

        Fragment() {
            match userIdOpt with
            | Some userId ->
                let shortId = if userId.Length > 8 then userId.Substring(0, 8) else userId
                header (class' = "page-header") {
                    span (class' = "user-identity") { shortId }
                }
            | None -> ()

            main () { content }
        }

    let htmlWithStream (ctx: HttpContext) (streamUrl: string) (content: HtmlElement) =
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

                // CSS styles will be added here

                if jsEnabled then
                    script (
                        type' = "module",
                        src = "https://cdn.jsdelivr.net/gh/starfederation/datastar@v1.0.0-RC.7/bundles/datastar.js",
                        crossorigin = "anonymous"
                    )
            }

            let bodyEl =
                if jsEnabled then body().attr("data-init", sprintf "@get('%s')" streamUrl)
                else body ()
            bodyEl { mainLayout ctx content }
        }

    /// Default page: subscribes to the global dashboard stream.
    let html (ctx: HttpContext) (content: HtmlElement) =
        htmlWithStream ctx "/sse" content
