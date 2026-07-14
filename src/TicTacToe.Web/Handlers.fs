module TicTacToe.Web.Handlers

open System
open System.IO
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker.ViewEngine
open Frank.Datastar
open TicTacToe.Web.SseBroadcast
open TicTacToe.Web.templates.shared
open TicTacToe.Web.templates.game
open TicTacToe.Engine
open TicTacToe.Model
open TicTacToe.Web.Model
open TicTacToe.Web.Surface

/// Signals type for move data from Datastar
[<CLIMutable>]
type MoveSignals =
    { gameId: string
      player: string
      position: string }

/// The surface cell this process was booted on (TICTACTOE_CELL; full surface by default).
let private surfaceOf (ctx: HttpContext) =
    ctx.RequestServices.GetRequiredService<Surface>()

/// /games and /arenas are ALIASES of one resource served by one handler stack. A representation
/// served under one name links and forms under THAT name, so a client that entered through the
/// banked /arenas surface never gets bounced onto the product's /games name mid-episode.
let routePrefix (ctx: HttpContext) =
    if ctx.Request.Path.Value.StartsWith "/arenas" then "/arenas" else "/games"

// ============================================================================
// Sd: semantic-discovery headers. So: ontology typing (httpRange-14).
// ============================================================================

/// State-dependent Allow for a game resource.
let private gameAllow (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "GET, POST, DELETE, OPTIONS"
    | _ -> "GET, DELETE, OPTIONS"   // terminal: no further moves

let private setDiscoveryHeaders (ctx: HttpContext) (selfPath: string) (allow: string option) =
    ctx.Response.Headers.Append("Link", $"</profile>; rel=\"profile\", <{selfPath}>; rel=\"self\"")
    match allow with
    | Some a -> ctx.Response.Headers.Append("Allow", a)
    | None -> ()

/// So: the game URI names the Game (the thing); its RDF description is a distinct document
/// (httpRange-14). Advertise it with a describedby Link on the HTML representation. Independent
/// of Sd's /profile Link — both coexist as separate Link headers when Sd+So.
let private setDescribedByHeader (ctx: HttpContext) (gameId: string) =
    ctx.Response.Headers.Append("Link", $"<{routePrefix ctx}/{gameId}/type>; rel=\"describedby\"")

/// So content negotiation: an agent asking for RDF (application/ld+json) is 303-redirected from
/// the thing (the game) to its describing document (/games/{id}/type).
let private acceptsLdJson (ctx: HttpContext) =
    ctx.Request.Headers.Accept.ToString().Contains "application/ld+json"

/// A client that cannot consume an event stream must never be handed one: an SSE response to a
/// plain GET holds the connection open until the caller's own timeout (a wasted turn for a
/// non-streaming agent). The stream is a stream — say so, and point back at the resource that
/// answers a GET. Enforced in middleware (Program.useStreamGuard), because the datastar handler
/// flushes stream headers the moment it is entered.
let acceptsEventStream (ctx: HttpContext) =
    ctx.Request.Headers.ContainsKey "datastar-request"
    || ctx.Request.Headers.Accept.ToString().Contains "text/event-stream"

/// 406 for a non-streaming caller of a stream endpoint, naming the media type and linking the
/// resource that does answer a plain GET.
let rejectNonStream (ctx: HttpContext) (canonical: string) =
    task {
        ctx.Response.StatusCode <- 406
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        ctx.Response.Headers.Append("Link", $"<{canonical}>; rel=\"canonical\"")
        do! ctx.Response.WriteAsync $"This endpoint serves text/event-stream. GET {canonical} for the current state."
    }

// Active game subscriptions - maps gameId to subscription disposable
let private gameSubscriptions =
    System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable>()

/// True when the client wants an html Post/Redirect/Get response rather than the datastar/API
/// one. A datastar (JS) request always carries the `datastar-request` header and an Accept that
/// happens to list text/html (so Accept alone is ambiguous) — it wants its SSE/API response, so
/// it is excluded first. Everything else gets html when it submits a form or explicitly accepts
/// html; a form body with no Accept (curl -d, the integration tests) falls back to html too.
let private wantsHtmlResponse (ctx: HttpContext) =
    if ctx.Request.Headers.ContainsKey "datastar-request" then
        false
    else
        ctx.Request.HasFormContentType || ctx.Request.Headers.Accept.ToString().Contains "text/html"

/// Stable slug for a move rejection — used both as the no-JS ?error= flash token and to
/// derive the datastar rejection-animation class.
let private rejectionSlug =
    function
    | NotYourTurn -> "not-your-turn"
    | NotAPlayer -> "not-a-player"
    | WrongPlayer -> "wrong-player"
    | GameOver -> "game-over"

/// Wrap a page in a no-JS error banner when the request carries an ?error= flash token
/// (set by a Post/Redirect/Get write that was rejected), otherwise the page unchanged.
let private withBanner (surface: Surface) (ctx: HttpContext) (content: HtmlElement) : HtmlElement =
    match ctx.Request.Query.TryGetValue("error") with
    | true, v when v.Count > 0 -> Fragment() { renderErrorBanner surface (string v.[0]); content } :> HtmlElement
    | _ -> content

let private etagOf (s: string) =
    use sha = System.Security.Cryptography.SHA256.Create()
    let h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes s)
    sprintf "\"%s\"" (h.[..7] |> Array.map (sprintf "%02x") |> String.concat "")

/// Render the game page and answer with it. The ETag is taken over the exact rendered bytes —
/// view-complete (board, turn, viewer role, surface) — and is baseline HTTP hygiene on ALL cells,
/// never per-factor (a per-factor ETag would confound cross-cell read friction): it lets a polling
/// agent skip re-reading unchanged HTML via a 304, the cheap change-signal for its wait loop.
let private renderGamePage (ctx: HttpContext) (gameId: string) (result: MoveResult) (errorMsg: string option) =
    task {
        let surface = surfaceOf ctx
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        let assignment = assignmentManager.GetAssignment(gameId)
        let gameCount = supervisor.GetActiveGameCount()
        let basePath = routePrefix ctx
        let board = renderGameBoard surface basePath gameId result userId assignment gameCount
        let body =
            match errorMsg with
            | Some msg -> Fragment() { renderErrorBanner surface msg; board } :> HtmlElement
            | None -> board |> withBanner surface ctx
        let element = layout.htmlWithStream ctx (sprintf "%s/%s/sse" basePath gameId) body
        use sw = new StringWriter()
        do! Render.toTextWriterAsync sw element
        let html = sw.ToString()
        let etag = etagOf html
        ctx.Response.Headers.ETag <- Microsoft.Extensions.Primitives.StringValues etag
        if ctx.Request.Method = "GET" && ctx.Request.Headers.IfNoneMatch.ToString() = etag then
            ctx.Response.StatusCode <- 304
        else
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            do! ctx.Response.WriteAsync html
    }

/// Subscribe to a game's state changes and broadcast updates
let subscribeToGame (surface: Surface) (gameId: string) (game: Game) (assignmentManager: PlayerAssignmentManager) (supervisor: GameSupervisor) =
    if not (gameSubscriptions.ContainsKey(gameId)) then
        let subscription =
            game.Subscribe(
                { new IObserver<MoveResult> with
                    member _.OnNext(result) =
                        // Render per-role and broadcast to each subscriber based on their role
                        let assignment = assignmentManager.GetAssignment(gameId)
                        let gameCount = supervisor.GetActiveGameCount()
                        let renderForRole userId =
                            let element = renderGameBoard surface "/games" gameId result userId assignment gameCount
                            PatchElements (fun tw -> Render.toTextWriterAsync tw element)
                        broadcastPerRoleForGame gameId renderForRole

                    member _.OnError(_) = ()

                    member _.OnCompleted() =
                        // Game completed - remove subscription
                        match gameSubscriptions.TryRemove(gameId) with
                        | true, sub -> sub.Dispose()
                        | _ -> () }
            )

        gameSubscriptions.TryAdd(gameId, subscription) |> ignore

/// Login endpoint - signs in user and redirects back
/// This creates a persistent cookie for user identification
let login (ctx: HttpContext) =
    task {
        // Check if already authenticated
        let! authResult = ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)

        if not authResult.Succeeded then
            // Create a new user identity with a unique ID
            let userId = Guid.NewGuid().ToString()
            let claims = [|
                System.Security.Claims.Claim(ClaimTypes.UserId, userId)
                System.Security.Claims.Claim(ClaimTypes.Created, DateTimeOffset.UtcNow.ToString("o"))
            |]
            let identity = System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)
            let principal = System.Security.Claims.ClaimsPrincipal(identity)

            // Sign in the user
            do! ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                AuthenticationProperties(IsPersistent = true))

        // Redirect back to the return URL or home
        let returnUrl =
            match ctx.Request.Query.TryGetValue("returnUrl") with
            | true, values when values.Count > 0 -> values.[0]
            | _ -> "/"

        ctx.Response.Redirect(returnUrl)
    }

/// Debug endpoint to show claims (temporary)
let debug (ctx: HttpContext) =
    task {
        // Explicitly try to authenticate
        let! authResult = ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)

        let claims = ctx.User.Claims |> Seq.map (fun c -> $"{c.Type}: {c.Value}") |> String.concat "\n"
        let userId = ctx.User.TryGetUserId()
        let isAuth = ctx.User.Identity.IsAuthenticated

        let authClaims =
            if authResult.Succeeded then
                authResult.Principal.Claims |> Seq.map (fun c -> $"{c.Type}: {c.Value}") |> String.concat "\n"
            else
                $"Auth failed: {authResult.Failure}"

        let response = $"IsAuthenticated: {isAuth}\nUserId: {userId}\nClaims:\n{claims}\n\nExplicit Auth Result:\n{authClaims}"
        ctx.Response.ContentType <- "text/plain"
        do! ctx.Response.WriteAsync(response)
    }

/// Logout endpoint - signs out user and removes cookie
let logout (ctx: HttpContext) =
    task {
        do! ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)

        // Redirect to home or return URL
        let returnUrl =
            match ctx.Request.Query.TryGetValue("returnUrl") with
            | true, values when values.Count > 0 -> values.[0]
            | _ -> "/"

        ctx.Response.Redirect(returnUrl)
    }

/// Home page handler — server-renders the full dashboard (all active games) so it is
/// discoverable and playable with no JS. Live updates after initial load come via the
/// dedicated /sse stream. Reads live state via GetState (no read-subscription).
let home (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let limits = ctx.RequestServices.GetRequiredService<GameLimits>()
        let surface = surfaceOf ctx
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        // One round-trip for all games' current state (avoids the per-game N+1 actor traffic
        // and the GetGame/GetState TOCTOU); kept eager so actor errors surface before flush.
        let snapshot = supervisor.SnapshotActiveGames()
        let gameCount = List.length snapshot
        let allowCreate =
            match limits.MaxGames with
            | Some m -> gameCount < m
            | None -> true
        let boards =
            snapshot
            |> List.map (fun (gameId, result) ->
                let assignment = assignmentManager.GetAssignment(gameId)
                renderGameBoard surface "/games" gameId result userId assignment gameCount)
        // Dynamic, per-viewer (role-personalised) representation: never cache, and Vary on
        // Cookie so an intermediary can't serve one user's board to another.
        ctx.Response.Headers.CacheControl <- "no-cache, private"
        ctx.Response.Headers.Vary <- "Cookie"
        if surface.Sd then setDiscoveryHeaders ctx "/" None
        let element = templates.home.homePage surface ctx allowCreate boards |> withBanner surface ctx |> layout.html ctx
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        do! Render.toStreamAsync ctx.Response.Body element
    }

/// SSE endpoint - sends game state updates to all connected clients
let sse (ctx: HttpContext) =
    task {
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        let (myChannel, subscription) = subscribe userId None
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let surface = surfaceOf ctx

        try
            // Send all existing games to the connecting client, personalized to their role.
            // One snapshot round-trip instead of a GetGame/GetState per game (no N+1, and no
            // hang if a game is mid-disposal).
            let snapshot = supervisor.SnapshotActiveGames()
            let gameCount = List.length snapshot
            for (gameId, state) in snapshot do
                let assignment = assignmentManager.GetAssignment(gameId)
                let element = renderGameBoard surface "/games" gameId state userId assignment gameCount
                do! Datastar.streamPatchElements (fun tw -> Render.toTextWriterAsync tw element) ctx

            // Keep connection open, forwarding all broadcast events
            while not ctx.RequestAborted.IsCancellationRequested do
                let! event = myChannel.Reader.ReadAsync(ctx.RequestAborted).AsTask()
                do! writeSseEvent ctx event
        with
        | :? OperationCanceledException -> ()
        | :? ChannelClosedException -> ()
        | _ -> ()

        subscription.Dispose()
    }

/// Per-game SSE endpoint — streams ONLY this game's events. On connect it sends a
/// morph-by-id snapshot of the current board (connect-resync, no container clear), then
/// forwards that game's broadcasts. Mirrors the dashboard `sse` but filtered to one game.
let private streamGame (ctx: HttpContext) (gameId: string) (userId: string) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let surface = surfaceOf ctx
        let (myChannel, subscription) = subscribe userId (Some gameId)

        try
            // Connect-resync: send the current board so a connect-after-move shows current state.
            match supervisor.TryGetState(gameId) with
            | Some result ->
                let assignment = assignmentManager.GetAssignment(gameId)
                let gameCount = supervisor.GetActiveGameCount()
                let element = renderGameBoard surface (routePrefix ctx) gameId result userId assignment gameCount
                do! Datastar.streamPatchElements (fun tw -> Render.toTextWriterAsync tw element) ctx
            | None -> ()

            while not ctx.RequestAborted.IsCancellationRequested do
                let! event = myChannel.Reader.ReadAsync(ctx.RequestAborted).AsTask()
                do! writeSseEvent ctx event
        with
        | :? OperationCanceledException -> ()
        | :? ChannelClosedException -> ()
        | _ -> ()

        subscription.Dispose()
    }

let gameSse (ctx: HttpContext) =
    task {
        let gameId = ctx.Request.RouteValues.["id"] |> string
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        do! streamGame ctx gameId userId
    }


// ============================================================================
// REST API Handlers for Multi-Game Support
// ============================================================================

/// POST /games - Create a new game
let createGame (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let limits = ctx.RequestServices.GetRequiredService<GameLimits>()
        let surface = surfaceOf ctx
        // No-JS form POST gets Post/Redirect/Get; datastar/API keeps status+Location.
        let wantsHtml = wantsHtmlResponse ctx

        // Atomic cap check + create: counting and creating happen on one supervisor turn,
        // so two concurrent POSTs at the boundary can't both pass and exceed MaxGames.
        match supervisor.TryCreateGame(limits.MaxGames) with
        | None when wantsHtml ->
            // Creation is not available at the game cap (the twin's wire: 409, not a redirect).
            ctx.Response.StatusCode <- 409
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            do! ctx.Response.WriteAsync "Max games reached."
        | None ->
            // Uniform interface: creation is not available at the game cap.
            ctx.Response.StatusCode <- 409
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync({| error = "MaxGamesReached" |})
        | Some(gameId, game) ->
            // Subscribe to game state changes
            subscribeToGame surface gameId game assignmentManager supervisor

            // Get initial state and broadcast to all clients
            use initialSub =
                game.Subscribe(
                    { new IObserver<MoveResult> with
                        member _.OnNext(result) =
                            let gameCount = supervisor.GetActiveGameCount()
                            let element = renderGameBoard surface "/games" gameId result "" None gameCount
                            broadcastToDashboard (PatchElementsAppend("#games-container", fun tw -> Render.toTextWriterAsync tw element))

                        member _.OnError(_) = ()
                        member _.OnCompleted() = () }
                )

            if wantsHtml then
                // No-JS: redirect to the dashboard so the new game is visible server-rendered.
                ctx.Response.StatusCode <- 303
                ctx.Response.Headers.Location <- "/"
            else
                // Return 201 Created with Location header
                ctx.Response.StatusCode <- 201
                ctx.Response.Headers.Location <- $"{routePrefix ctx}/{gameId}"
    }

/// GET /games/{id} - Get a specific game
let getGame (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let surface = surfaceOf ctx
        let gameId = ctx.Request.RouteValues.["id"] |> string

        // GET is safe: no subscription side effect here. Every game is subscribed for
        // broadcasts at creation (createGame / startup / reset), so a read need not register one.
        // Read from the supervisor's cached state (TryGetState): never messages the game actor,
        // so a just-deleted game returns None (404) instead of hanging on a Stopped actor.
        let basePath = routePrefix ctx
        match supervisor.TryGetState(gameId) with
        | Some _ when surface.So && acceptsLdJson ctx ->
            // httpRange-14: an RDF request on the thing → 303 See Other to its describing document.
            ctx.Response.StatusCode <- 303
            ctx.Response.Headers.Location <- Microsoft.Extensions.Primitives.StringValues $"{basePath}/{gameId}/type"
        | Some result ->
            ctx.Response.Headers.CacheControl <- "no-cache, private"
            ctx.Response.Headers.Vary <- "Cookie"
            if surface.Sd then setDiscoveryHeaders ctx $"{basePath}/{gameId}" (Some(gameAllow result))
            if surface.So then setDescribedByHeader ctx gameId
            do! renderGamePage ctx gameId result None
        | None ->
            ctx.Response.StatusCode <- 404
            ctx.Response.Headers.CacheControl <- "no-cache, private"
            ctx.Response.Headers.Vary <- "Cookie"
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            let element = notFoundPage |> layout.html ctx
            do! Render.toStreamAsync ctx.Response.Body element
    }

/// Helper to determine if it's X's turn based on game state
let private isXTurn (moveResult: MoveResult) =
    match moveResult with
    | MoveResult.XTurn _ -> true
    | _ -> false

/// POST /games/{id} - Make a move in a specific game
let makeMove (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()

        let assignmentManager =
            ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()

        let eventLog = ctx.RequestServices.GetRequiredService<TicTacToe.Web.EventLog.EventLog>()

        let gameId = ctx.Request.RouteValues.["id"] |> string

        // Get user ID from authenticated user
        let userId = ctx.User.TryGetUserId()

        // Content-Type drives body parsing; Accept drives the response shape. A native form
        // POST (no JS) gets Post/Redirect/Get: 303 back to the game so a refresh shows current
        // state. A datastar request keeps 202 (its SSE stream updates the board).
        let isForm = ctx.Request.HasFormContentType
        let wantsHtml = wantsHtmlResponse ctx

        match supervisor.GetGame(gameId), userId with
        | Some game, Some uid ->
            // The move is a command. Progressive enhancement: a plain form POST
            // (no JS) carries it as form fields; datastar sends it as signals.
            // Branch on content type to avoid double-reading the request body.
            let! parsedMove =
                if isForm then
                    task {
                        let field (name: string) =
                            match ctx.Request.Form.TryGetValue(name) with
                            | true, v -> string v
                            | _ -> ""
                        return Move.TryParse(field "player", field "position")
                    }
                else
                    task {
                        let! signals = Datastar.tryReadSignals<MoveSignals> ctx
                        return
                            match signals with
                            | ValueSome s -> Move.TryParse(s.player, s.position)
                            | ValueNone -> None
                    }

            match parsedMove with
            | None ->
                // Malformed move (bad player/position token, or a non-form body) — a client error.
                ctx.Response.StatusCode <- 400
                eventLog.LogEvent("move_rejected", gameId, role = "unassigned", reason = "InvalidMove")
                if wantsHtml then do! renderGamePage ctx gameId (game.GetState()) (Some "invalid-move")
            | Some moveAction ->
                // Get current game state to determine whose turn it is
                let currentState = game.GetState()
                let xTurn = isXTurn currentState

                // Snapshot seats before assignment so we can detect a first seat claim.
                let before = assignmentManager.GetAssignment(gameId)
                let hadSeat (a: PlayerAssignment option) (uid: string) =
                    match a with
                    | Some a -> a.PlayerXId = Some uid || a.PlayerOId = Some uid
                    | None -> false

                // Validate and potentially assign player
                let (validationResult, assignment) =
                    assignmentManager.TryAssignAndValidate(gameId, uid, xTurn)

                let role =
                    if assignment.PlayerXId = Some uid then "X"
                    elif assignment.PlayerOId = Some uid then "O"
                    else "unassigned"

                match validationResult with
                | Allowed ->
                    if not (hadSeat before uid) && (role = "X" || role = "O") then
                        eventLog.LogEvent("player_assigned", gameId, role = role)
                    // Command accepted; the new board is projected via the SSE event stream.
                    subscribeToGame (surfaceOf ctx) gameId game assignmentManager supervisor
                    game.MakeMove(moveAction)
                    let movePos = match moveAction with | XMove p | OMove p -> p
                    let after = game.GetState()
                    // TryAssignAndValidate only checks player + turn; the engine still rejects
                    // an occupied square (keeps prior state, fire-and-forget). A move applied
                    // iff it advanced the turn or ended the game — a same-turn result means it
                    // was rejected (covers an opponent's taken square AND replaying your own,
                    // which "is the square mine?" could not distinguish).
                    let applied =
                        match currentState, after with
                        | XTurn _, XTurn _ | OTurn _, OTurn _ -> false
                        | _, Error _ -> false
                        | _ -> true
                    if applied then
                        eventLog.LogEvent("move_accepted", gameId, role = role, move = movePos.ToString())
                        match after with
                        | Won(gs, winner) ->
                            let moveCount = gs |> Seq.filter (fun kv -> kv.Value <> Empty) |> Seq.length
                            eventLog.LogEvent("game_over", gameId, outcome = $"{winner.ToString().ToLower()}_wins", moveCount = moveCount)
                        | Draw _ ->
                            eventLog.LogEvent("game_over", gameId, outcome = "draw", moveCount = 9)
                        | _ -> ()
                        // Accepted: the fresh board IS the response (200 + representation). A
                        // datastar client is updated by its SSE stream instead (202).
                        if wantsHtml then do! renderGamePage ctx gameId after None
                        else ctx.Response.StatusCode <- 202
                    else
                        // The seat and the turn were fine; the ENGINE refused the square. That is an
                        // unprocessable move, not a routing or authorization error: 422, half of the
                        // illegalMoves DV (the other half is 403 out-of-turn).
                        eventLog.LogEvent("move_rejected", gameId, role = role, reason = "PositionTaken")
                        ctx.Response.StatusCode <- 422
                        if wantsHtml then do! renderGamePage ctx gameId currentState (Some "position-taken")
                | Rejected reason ->
                    eventLog.LogEvent("move_rejected", gameId, role = role, reason = reason.ToString())
                    let slug = rejectionSlug reason
                    // Out-of-turn / not-a-player / game-over: the caller may not act here. 403.
                    ctx.Response.StatusCode <- 403
                    if wantsHtml then
                        do! renderGamePage ctx gameId currentState (Some slug)
                    else
                        // Move was rejected - broadcast rejection animation
                        broadcast (PatchSignals $"""{{ "rejectionAnimation": "rejection-{slug}" }}""")
        | None, _ -> ctx.Response.StatusCode <- 404
        | _, None ->
            // No user ID - cannot make moves without authentication
            ctx.Response.StatusCode <- 401
    }

/// DELETE /games/{id} - Delete a game
let deleteGame (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let gameId = ctx.Request.RouteValues.["id"] |> string

        // Get user ID from authenticated user
        let userId = ctx.User.TryGetUserId()
        let wantsHtml = wantsHtmlResponse ctx

        match supervisor.GetGame(gameId), userId with
        | _, _ when gameLocked () ->
            // Locked experiment game: immutable to agents (no delete-and-replace contamination).
            ctx.Response.StatusCode <- 409
        | Some game, Some uid ->
            // Check if deleting would reduce count below 6
            let gameCount = supervisor.GetActiveGameCount()
            if gameCount <= 6 then
                ctx.Response.StatusCode <- 409  // Conflict - would drop below minimum
            else
                // Check authorization - must be an assigned player
                let assignment = assignmentManager.GetAssignment(gameId)
                match assignment with
                | Some a when a.PlayerXId = Some uid || a.PlayerOId = Some uid ->
                    // Clear player assignments
                    assignmentManager.RemoveGame(gameId)

                    // Dispose the game - this triggers OnCompleted which removes subscription
                    game.Dispose()

                    // Broadcast removal to all clients
                    broadcast (RemoveElement $"#game-{gameId}")

                    if wantsHtml then
                        // No-JS POST alias: redirect to the dashboard.
                        ctx.Response.StatusCode <- 303
                        ctx.Response.Headers.Location <- "/"
                    else
                        ctx.Response.StatusCode <- 204
                | _ ->
                    ctx.Response.StatusCode <- 403  // Forbidden - not an assigned player
        | None, _ -> ctx.Response.StatusCode <- 404
        | _, None -> ctx.Response.StatusCode <- 401  // Unauthorized - no user
    }

/// POST /games/{id}/reset - Reset a game (create new game in same position)
let resetGame (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let gameId = ctx.Request.RouteValues.["id"] |> string

        // Get user ID from authenticated user
        let userId = ctx.User.TryGetUserId()
        let wantsHtml = wantsHtmlResponse ctx

        match supervisor.GetGame(gameId), userId with
        | _, _ when gameLocked () ->
            // Locked experiment game: an agent cannot clear the board and replay it.
            ctx.Response.StatusCode <- 409
        | Some oldGame, Some uid ->
            // Check authorization - must be an assigned player
            let currentState = oldGame.GetState()
            let assignment = assignmentManager.GetAssignment(gameId)
            match assignment with
            | Some a when a.PlayerXId = Some uid || a.PlayerOId = Some uid ->
                let hasActivity = hasGameActivity currentState assignment

                if not hasActivity then
                    // Cannot reset a game with no activity
                    ctx.Response.StatusCode <- 403
                else
                    // Create new game first (maintains count)
                    let (newGameId, newGame) = supervisor.CreateGame()

                    // Subscribe to new game state changes
                    subscribeToGame (surfaceOf ctx) newGameId newGame assignmentManager supervisor

                    // Clear old game's player assignments
                    assignmentManager.RemoveGame(gameId)

                    // Dispose old game
                    oldGame.Dispose()

                    // Broadcast replacement: remove old game, add new game
                    broadcast (RemoveElement $"#game-{gameId}")

                    // Get initial state and broadcast new game
                    use initialSub =
                        newGame.Subscribe(
                            { new IObserver<MoveResult> with
                                member _.OnNext(result) =
                                    let gameCount = supervisor.GetActiveGameCount()
                                    let element = renderGameBoard (surfaceOf ctx) "/games" newGameId result "" None gameCount
                                    broadcastToDashboard (PatchElementsAppend("#games-container", fun tw -> Render.toTextWriterAsync tw element))

                                member _.OnError(_) = ()
                                member _.OnCompleted() = () }
                        )

                    if wantsHtml then
                        // No-JS form: redirect to the freshly reset game.
                        ctx.Response.StatusCode <- 303
                        ctx.Response.Headers.Location <- $"{routePrefix ctx}/{newGameId}"
                    else
                        // Reset creates a new resource; 201 Created with its Location (200 + Location
                        // is undefined for a body-less response).
                        ctx.Response.StatusCode <- 201
                        ctx.Response.Headers.Location <- $"{routePrefix ctx}/{newGameId}"
            | _ ->
                ctx.Response.StatusCode <- 403  // Forbidden - not an assigned player
        | None, _ -> ctx.Response.StatusCode <- 404
        | _, None -> ctx.Response.StatusCode <- 401  // Unauthorized - no user
    }

// ============================================================================
// Sd / So: discovery documents (404 when the factor is off — the surface is the toggle)
// ============================================================================

/// GET /profile — ALPS profile of the app's affordances (Sd only).
let profile (ctx: HttpContext) =
    task {
        let surface = surfaceOf ctx
        if not surface.Sd then
            ctx.Response.StatusCode <- 404
        else
            setDiscoveryHeaders ctx "/profile" (Some "GET, OPTIONS")
            ctx.Response.ContentType <- "application/alps+json; charset=utf-8"
            do! ctx.Response.WriteAsync Discovery.alpsProfile
    }

/// GET /.well-known/home — JSON Home document (Sd only).
let wellKnownHome (ctx: HttpContext) =
    task {
        let surface = surfaceOf ctx
        if not surface.Sd then
            ctx.Response.StatusCode <- 404
        else
            setDiscoveryHeaders ctx "/.well-known/home" (Some "GET, OPTIONS")
            ctx.Response.ContentType <- "application/json-home; charset=utf-8"
            do! ctx.Response.WriteAsync Discovery.jsonHome
    }

/// GET /games/{id}/type — the game's RDF description as schema.org/Game JSON-LD (So only).
/// The document the game URI (the thing) dereferences to under content negotiation.
let gameType (ctx: HttpContext) =
    task {
        let surface = surfaceOf ctx
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let gameId = ctx.Request.RouteValues.["id"] |> string
        match surface.So, supervisor.TryGetState(gameId) with
        | false, _ | _, None ->
            ctx.Response.StatusCode <- 404
        | true, Some _ ->
            let gameUri = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}{routePrefix ctx}/{gameId}"
            ctx.Response.ContentType <- "application/ld+json; charset=utf-8"
            do! ctx.Response.WriteAsync(Discovery.gameJsonLd gameUri)
    }
