module TicTacToe.Web.Simple.Handlers

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Simple.GameStore
open TicTacToe.Web.Simple.Model
open TicTacToe.Web.Simple.templates.shared
open TicTacToe.Web.Simple.templates.game
open TicTacToe.Web.Simple.templates.home

// ============================================================================
// Content Negotiation
// ============================================================================

let private acceptsJson (ctx: HttpContext) =
    ctx.Request.Headers.Accept
    |> Seq.exists (fun v -> v.Contains("application/json"))

// ============================================================================
// Auth
// ============================================================================

/// GET /login — sign in and redirect back
let login (ctx: HttpContext) =
    task {
        let! authResult = ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)

        if not authResult.Succeeded then
            let userId = Guid.NewGuid().ToString()
            let claims =
                [| System.Security.Claims.Claim(ClaimTypes.UserId, userId)
                   System.Security.Claims.Claim(ClaimTypes.Created, DateTimeOffset.UtcNow.ToString("o")) |]
            let identity = System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)
            let principal = System.Security.Claims.ClaimsPrincipal(identity)

            do! ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                AuthenticationProperties(IsPersistent = true))

        let returnUrl =
            match ctx.Request.Query.TryGetValue("returnUrl") with
            | true, values when values.Count > 0 -> values.[0]
            | _ -> "/"

        ctx.Response.Redirect(returnUrl)
    }

/// GET /logout — sign out and redirect
let logout (ctx: HttpContext) =
    task {
        do! ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)

        let returnUrl =
            match ctx.Request.Query.TryGetValue("returnUrl") with
            | true, values when values.Count > 0 -> values.[0]
            | _ -> "/"

        ctx.Response.Redirect(returnUrl)
    }

// ============================================================================
// Home
// ============================================================================

/// GET / — list all arenas
let home (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let arenas = store.List()
        let element = homePage ctx arenas |> layout.html ctx
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        do! Render.toStreamAsync ctx.Response.Body element
    }

// ============================================================================
// Arena helpers
// ============================================================================

let private isXTurn = function
    | XTurn _ -> true
    | _ -> false

let private renderArenaHtml (ctx: HttpContext) (arenaId: string) (result: MoveResult) (errorMsg: string option) =
    let store = ctx.RequestServices.GetRequiredService<GameStore>()
    let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
    let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
    let assignment = assignmentManager.GetAssignment(arenaId)
    let element = renderArenaPage arenaId result userId assignment errorMsg |> layout.html ctx
    ctx.Response.ContentType <- "text/html; charset=utf-8"
    Render.toStreamAsync ctx.Response.Body element

// ============================================================================
// Arena DTOs for JSON
// ============================================================================

[<CLIMutable>]
type ArenaJson =
    { id: string
      board: string[]
      status: string
      whoseTurn: string option }

let private toArenaJson (arenaId: string) (result: MoveResult) : ArenaJson =
    let board =
        [ TopLeft; TopCenter; TopRight
          MiddleLeft; MiddleCenter; MiddleRight
          BottomLeft; BottomCenter; BottomRight ]
        |> List.map (fun pos ->
            let (|State|) = function
                | XTurn(s, _) | OTurn(s, _) | Won(s, _) | Draw s | Error(s, _) -> s
            let (State state) = result
            match state.TryGetValue(pos) with
            | true, Taken X -> "X"
            | true, Taken O -> "O"
            | _ -> "")
        |> Array.ofList

    let status =
        match result with
        | XTurn _ -> "InProgress"
        | OTurn _ -> "InProgress"
        | Won(_, player) -> $"{player}Wins"
        | Draw _ -> "Draw"
        | Error _ -> "Error"

    let whoseTurn =
        match result with
        | XTurn _ -> Some "X"
        | OTurn _ -> Some "O"
        | _ -> None

    { id = arenaId; board = board; status = status; whoseTurn = whoseTurn }

// ============================================================================
// Arena CRUD
// ============================================================================

/// POST /arenas — create a new arena and redirect to it
let createArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let (arenaId, _) = store.Create()
        ctx.Response.Redirect($"/arenas/{arenaId}")
    }

/// GET /arenas/{id} — get arena (HTML or JSON)
let getArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string

        match store.Get(arenaId) with
        | None ->
            ctx.Response.StatusCode <- 404
        | Some result ->
            if acceptsJson ctx then
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsJsonAsync(toArenaJson arenaId result)
            else
                do! renderArenaHtml ctx arenaId result None
    }

/// POST /arenas/{id} — make a move
let makeMove (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string
        let userId = ctx.User.TryGetUserId()

        match store.Get(arenaId), userId with
        | None, _ ->
            ctx.Response.StatusCode <- 404
        | _, None ->
            ctx.Response.StatusCode <- 401
        | Some currentResult, Some uid ->
            // Parse form fields
            let playerRaw =
                match ctx.Request.Form.TryGetValue("player") with
                | true, v -> v.ToString()
                | _ -> ""
            let positionRaw =
                match ctx.Request.Form.TryGetValue("position") with
                | true, v -> v.ToString()
                | _ -> ""

            match Move.TryParse(playerRaw, positionRaw) with
            | None ->
                // Bad form data — re-render with error
                if acceptsJson ctx then
                    ctx.Response.StatusCode <- 400
                else
                    do! renderArenaHtml ctx arenaId currentResult (Some "Invalid move format.")
            | Some move ->
                let xTurn = isXTurn currentResult

                // Validate and potentially assign player
                let (validationResult, _) = assignmentManager.TryAssignAndValidate(arenaId, uid, xTurn)

                match validationResult with
                | Allowed ->
                    match store.Update(arenaId, move) with
                    | None ->
                        ctx.Response.StatusCode <- 404
                    | Some nextResult ->
                        if acceptsJson ctx then
                            ctx.Response.ContentType <- "application/json"
                            do! ctx.Response.WriteAsJsonAsync(toArenaJson arenaId nextResult)
                        else
                            do! renderArenaHtml ctx arenaId nextResult None

                | Rejected reason ->
                    let msg =
                        match reason with
                        | NotYourTurn -> "It's not your turn."
                        | NotAPlayer -> "You are not a player in this arena."
                        | WrongPlayer -> "You are not the correct player."
                        | GameOver -> "This game is already over."

                    if acceptsJson ctx then
                        ctx.Response.StatusCode <- 403
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsJsonAsync({| error = msg |})
                    else
                        // Re-render same page with inline error — do NOT hide buttons
                        do! renderArenaHtml ctx arenaId currentResult (Some msg)
    }

/// DELETE /arenas/{id} (via POST /arenas/{id}/delete form workaround)
let deleteArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string

        store.Delete(arenaId)
        assignmentManager.RemoveGame(arenaId)

        if acceptsJson ctx then
            ctx.Response.StatusCode <- 204
        else
            ctx.Response.Redirect("/")
    }

/// POST /arenas/{id}/restart — reset arena state
let restartArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string

        // Clear player assignments so new players can join
        assignmentManager.RemoveGame(arenaId)

        match store.Reset(arenaId) with
        | None ->
            ctx.Response.StatusCode <- 404
        | Some result ->
            if acceptsJson ctx then
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsJsonAsync(toArenaJson arenaId result)
            else
                ctx.Response.Redirect($"/arenas/{arenaId}")
    }
