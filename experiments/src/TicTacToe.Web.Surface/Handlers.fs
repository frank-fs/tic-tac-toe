module TicTacToe.Web.Surface.Handlers

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Surface.GameStore
open TicTacToe.Web.Surface.Logger
open TicTacToe.Web.Surface.Model
open TicTacToe.Web.Surface.Surface
open TicTacToe.Web.Surface.templates.shared
open TicTacToe.Web.Surface.templates.game
open TicTacToe.Web.Surface.templates.home

// ============================================================================
// Naive-HTML floor — every response is HTML, never a structured JSON payload
// ============================================================================

let private sessionId (ctx: HttpContext) =
    ctx.Request.Cookies.TryGetValue("TicTacToe.SimpleUser")
    |> function true, v -> v | _ -> "anonymous"

let private requestId () = Guid.NewGuid().ToString()

// ============================================================================
// Sd: semantic discovery header helpers
// ============================================================================

/// State-dependent Allow for an arena resource.
let private arenaAllow (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "GET, POST, DELETE, OPTIONS"
    | _ -> "GET, DELETE, OPTIONS"   // terminal: no further moves

let private setDiscoveryHeaders (ctx: HttpContext) (selfPath: string) (allow: string option) =
    ctx.Response.Headers.Append("Link", $"</profile>; rel=\"profile\", <{selfPath}>; rel=\"self\"")
    match allow with
    | Some a -> ctx.Response.Headers.Append("Allow", a)
    | None -> ()

/// So: advertise the domain-knowledge article as a followable link (the channel agents
/// actually use — inline JSON-LD was observed ignored). Independent of Sd's /profile.
let private setStrategyHeader (ctx: HttpContext) =
    ctx.Response.Headers.Append("Link", "</strategy>; rel=\"subjectOf\"")

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
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        let arenas = store.List()
        let allowCreate =
            match store.MaxGames with
            | Some m -> arenas.Length < m
            | None -> true
        if surface.Sd then setDiscoveryHeaders ctx "/" None
        let element = homePage surface ctx allowCreate arenas |> layout.html ctx
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        do! Render.toStreamAsync ctx.Response.Body element
    }

// ============================================================================
// Arena helpers
// ============================================================================

let private isXTurn = function
    | XTurn _ -> true
    | _ -> false

let private etagOf (s: string) =
    use sha = System.Security.Cryptography.SHA256.Create()
    let h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes s)
    sprintf "\"%s\"" (h.[..7] |> Array.map (sprintf "%02x") |> String.concat "")

let private renderArenaHtml (ctx: HttpContext) (arenaId: string) (result: MoveResult) (errorMsg: string option) =
    task {
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        let assignment = assignmentManager.GetAssignment(arenaId)
        let element = renderArenaPage surface arenaId result userId assignment errorMsg |> layout.html ctx
        use sw = new System.IO.StringWriter()
        do! Render.toTextWriterAsync sw element
        let html = sw.ToString()
        // ETag over the exact rendered bytes — view-complete (board, turn, viewer role, surface).
        // Baseline HTTP hygiene on ALL cells (never per-factor, else it confounds cross-cell
        // friction): lets a polling agent skip re-reading unchanged HTML via a 304, the cheap
        // change-signal for the wait loop.
        let etag = etagOf html
        ctx.Response.Headers.ETag <- Microsoft.Extensions.Primitives.StringValues etag
        if ctx.Request.Method = "GET" && ctx.Request.Headers.IfNoneMatch.ToString() = etag then
            ctx.Response.StatusCode <- 304
        else
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            do! ctx.Response.WriteAsync html
    }

// ============================================================================
// Arena CRUD
// ============================================================================

/// POST /arenas — create a new arena and redirect to it
let createArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let logger = ctx.RequestServices.GetRequiredService<RequestLogger>()
        let sid = sessionId ctx
        let rid = requestId ()
        match store.Create() with
        | None ->
            ctx.Response.StatusCode <- 409
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            do! ctx.Response.WriteAsync("Max games reached.")
            logger.LogRequest(rid, sid, None, "unassigned", "POST", "/arenas", 409, Some "MaxGamesReached", None, None)
        | Some (arenaId, _) ->
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", "/arenas", 302, None, None, None)
            logger.LogEvent("game_created", arenaId)
            ctx.Response.Redirect($"/arenas/{arenaId}")
    }

/// GET /arenas/{id} — render the arena board as HTML
let getArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string

        match store.Get(arenaId) with
        | None ->
            ctx.Response.StatusCode <- 404
        | Some result ->
            let surface = ctx.RequestServices.GetRequiredService<Surface>()
            if surface.Sd then setDiscoveryHeaders ctx $"/arenas/{arenaId}" (Some (arenaAllow result))
            if surface.So then setStrategyHeader ctx
            do! renderArenaHtml ctx arenaId result None
    }

/// POST /arenas/{id} — make a move
let makeMove (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let logger = ctx.RequestServices.GetRequiredService<RequestLogger>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string
        let userId = ctx.User.TryGetUserId()
        let sid = sessionId ctx
        let rid = requestId ()
        let path = $"/arenas/{arenaId}"

        match store.Get(arenaId), userId with
        | None, _ ->
            ctx.Response.StatusCode <- 404
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 404, None, None, None)
        | _, None ->
            ctx.Response.StatusCode <- 401
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 401, None, None, None)
        | Some currentResult, Some _ when not ctx.Request.HasFormContentType ->
            // Non-form body (JSON, query-string, empty) — reading ctx.Request.Form would throw
            // (HTTP 500). A malformed request is a client error: 400, not 500.
            ctx.Response.StatusCode <- 400
            do! renderArenaHtml ctx arenaId currentResult (Some "Invalid move format.")
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 400, Some "InvalidMove", Some currentResult, None)
            logger.LogEvent("move_rejected", arenaId, role = "unassigned", reason = "InvalidMove")
        | Some currentResult, Some uid ->
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
                ctx.Response.StatusCode <- 400
                do! renderArenaHtml ctx arenaId currentResult (Some "Invalid move format.")
                logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 400, Some "InvalidMove", Some currentResult, None)
                logger.LogEvent("move_rejected", arenaId, role = "unassigned", reason = "InvalidMove")
            | Some move ->
                let xTurn = isXTurn currentResult
                let before = assignmentManager.GetAssignment(arenaId)
                let (validationResult, assignment) = assignmentManager.TryAssignAndValidate(arenaId, uid, xTurn)

                let playerRole =
                    match assignment.PlayerXId, assignment.PlayerOId with
                    | Some xId, _ when xId = uid -> "X"
                    | _, Some oId when oId = uid -> "O"
                    | _ -> "unassigned"

                let hadSeat =
                    match before with
                    | Some a -> a.PlayerXId = Some uid || a.PlayerOId = Some uid
                    | None -> false
                if not hadSeat && (playerRole = "X" || playerRole = "O") then
                    logger.LogEvent("player_assigned", arenaId, role = playerRole)

                match validationResult with
                | Allowed ->
                    match store.Update(arenaId, move) with
                    | None ->
                        ctx.Response.StatusCode <- 404
                        logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 404, None, Some currentResult, None)
                    | Some (Error(_, _)) ->
                        ctx.Response.StatusCode <- 422
                        do! renderArenaHtml ctx arenaId currentResult (Some "That square is already taken.")
                        logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 422, Some "PositionTaken", Some currentResult, None)
                        logger.LogEvent("move_rejected", arenaId, role = playerRole, reason = "PositionTaken")
                    | Some nextResult ->
                        do! renderArenaHtml ctx arenaId nextResult None
                        // boardBefore is currentResult from store.Get above; store.Update is a separate call,
                        // so a concurrent move between the two can make boardBefore stale by one move.
                        logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 200, None, Some currentResult, Some nextResult)
                        logger.LogEvent("move_accepted", arenaId, role = playerRole, move = positionRaw)
                        match nextResult with
                        | Won(gs, winner) ->
                            let moveCount = gs |> Seq.filter (fun kv -> kv.Value <> Empty) |> Seq.length
                            logger.LogEvent("game_over", arenaId, outcome = $"{winner.ToString().ToLower()}_wins", moveCount = moveCount)
                        | Draw _ ->
                            logger.LogEvent("game_over", arenaId, outcome = "draw", moveCount = 9)
                        | _ -> ()

                | Rejected reason ->
                    let msg =
                        match reason with
                        | OutOfTurn -> "It's not your turn."
                        | NotAPlayer -> "You are not a player in this arena."
                        | PositionTaken -> "That square is already taken."
                        | GameOver -> "This game is already over."
                        | InvalidMove -> "Invalid move."

                    ctx.Response.StatusCode <- 403
                    do! renderArenaHtml ctx arenaId currentResult (Some msg)
                    logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 403, Some (reason.ToString()), Some currentResult, None)
                    logger.LogEvent("move_rejected", arenaId, role = playerRole, reason = reason.ToString())
    }

/// A finished arena (Won/Draw) is terminal: its post-game affordances are gated so an
/// agent cannot delete/restart it and spin up a replacement game that contaminates a run.
let private isTerminalResult (result: MoveResult) =
    match result with
    | Won _ | Draw _ -> true
    | _ -> false

/// When TICTACTOE_LOCK_GAME=1 the experiment game is immutable to agents: delete/restart always
/// 409, even mid-play. Prevents agent-triggered resets that clear the board and replay it,
/// corrupting a run — Sd/So cells discover /restart via the profile; others invent it.
let private gameLocked () =
    System.Environment.GetEnvironmentVariable "TICTACTOE_LOCK_GAME" = "1"

/// DELETE /arenas/{id} (via POST /arenas/{id}/delete form workaround)
let deleteArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string

        if gameLocked () then
            ctx.Response.StatusCode <- 409
        else
            match store.Get(arenaId) with
            | Some result when isTerminalResult result ->
                ctx.Response.StatusCode <- 409
            | _ ->
                store.Delete(arenaId)
                assignmentManager.RemoveGame(arenaId)
                ctx.Response.Redirect("/")
    }

/// POST /arenas/{id}/restart — reset arena state
let restartArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string

        if gameLocked () then
            ctx.Response.StatusCode <- 409
        else
            match store.Get(arenaId) with
            | Some result when isTerminalResult result ->
                ctx.Response.StatusCode <- 409
            | _ ->
                // Clear player assignments so new players can join
                assignmentManager.RemoveGame(arenaId)
                match store.Reset(arenaId) with
                | None ->
                    ctx.Response.StatusCode <- 404
                | Some _ ->
                    ctx.Response.Redirect($"/arenas/{arenaId}")
    }

// ============================================================================
// Sd: discovery document resources
// ============================================================================

/// GET /profile — ALPS profile of the app's affordances (Sd only).
let profile (ctx: HttpContext) =
    task {
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.Sd then
            ctx.Response.StatusCode <- 404
        else
            setDiscoveryHeaders ctx "/profile" (Some "GET, OPTIONS")
            if surface.So then setStrategyHeader ctx
            ctx.Response.ContentType <- "application/alps+json; charset=utf-8"
            do! ctx.Response.WriteAsync TicTacToe.Web.Surface.Discovery.alpsProfile
    }

/// GET /.well-known/home — JSON Home document (Sd only).
let wellKnownHome (ctx: HttpContext) =
    task {
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.Sd then
            ctx.Response.StatusCode <- 404
        else
            setDiscoveryHeaders ctx "/.well-known/home" (Some "GET, OPTIONS")
            ctx.Response.ContentType <- "application/json-home; charset=utf-8"
            do! ctx.Response.WriteAsync TicTacToe.Web.Surface.Discovery.jsonHome
    }

/// GET /strategy — the domain-strategy article the So ontology links to (So only).
/// Reachable only by an agent that read the ontology block and chose to follow it:
/// tests reaching-for-knowledge, not spoon-feeding. Absent from non-So surfaces (404).
let strategy (ctx: HttpContext) =
    task {
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.So then
            ctx.Response.StatusCode <- 404
        else
            let element =
                article () {
                    h1 () { "Tic-tac-toe strategy" }
                    p () { "With perfect play by both sides, tic-tac-toe is a draw: a careful player never has to lose." }
                    ol () {
                        li () { "If you can place your mark to complete a line of three this turn, do it — that wins." }
                        li () { "Otherwise, if your opponent already has two of their marks in a line with the third cell empty, play that cell to block them." }
                        li () { "With no immediate win or threat, prefer the center, then a corner." }
                        li () { "Beware a fork — a position giving your opponent two separate threats at once cannot be blocked; prevent it before it forms." }
                    }
                }
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            do! Render.toStreamAsync ctx.Response.Body element
    }
