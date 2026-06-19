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

/// Signals type for move data from Datastar
[<CLIMutable>]
type MoveSignals =
    { gameId: string
      player: string
      position: string }

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
let private withBanner (ctx: HttpContext) (content: HtmlElement) : HtmlElement =
    match ctx.Request.Query.TryGetValue("error") with
    | true, v when v.Count > 0 -> Fragment() { renderErrorBanner (string v.[0]); content } :> HtmlElement
    | _ -> content

/// Subscribe to a game's state changes and broadcast updates
let subscribeToGame (gameId: string) (game: Game) (assignmentManager: PlayerAssignmentManager) (supervisor: GameSupervisor) =
    if not (gameSubscriptions.ContainsKey(gameId)) then
        let subscription =
            game.Subscribe(
                { new IObserver<MoveResult> with
                    member _.OnNext(result) =
                        // Render per-role and broadcast to each subscriber based on their role
                        let assignment = assignmentManager.GetAssignment(gameId)
                        let gameCount = supervisor.GetActiveGameCount()
                        let renderForRole userId =
                            let element = renderGameBoard gameId result userId assignment gameCount
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
                renderGameBoard gameId result userId assignment gameCount)
        // Dynamic, per-viewer (role-personalised) representation: never cache, and Vary on
        // Cookie so an intermediary can't serve one user's board to another.
        ctx.Response.Headers.CacheControl <- "no-cache, private"
        ctx.Response.Headers.Vary <- "Cookie"
        let element = templates.home.homePage ctx allowCreate boards |> withBanner ctx |> layout.html ctx
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

        try
            // Send all existing games to the connecting client, personalized to their role.
            // One snapshot round-trip instead of a GetGame/GetState per game (no N+1, and no
            // hang if a game is mid-disposal).
            let snapshot = supervisor.SnapshotActiveGames()
            let gameCount = List.length snapshot
            for (gameId, state) in snapshot do
                let assignment = assignmentManager.GetAssignment(gameId)
                let element = renderGameBoard gameId state userId assignment gameCount
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
let gameSse (ctx: HttpContext) =
    task {
        let gameId = ctx.Request.RouteValues.["id"] |> string
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        let (myChannel, subscription) = subscribe userId (Some gameId)
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()

        try
            // Connect-resync: send the current board so a connect-after-move shows current state.
            match supervisor.TryGetState(gameId) with
            | Some result ->
                let assignment = assignmentManager.GetAssignment(gameId)
                let gameCount = supervisor.GetActiveGameCount()
                let element = renderGameBoard gameId result userId assignment gameCount
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


// ============================================================================
// REST API Handlers for Multi-Game Support
// ============================================================================

/// POST /games - Create a new game
let createGame (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let limits = ctx.RequestServices.GetRequiredService<GameLimits>()
        // No-JS form POST gets Post/Redirect/Get; datastar/API keeps status+Location.
        let wantsHtml = wantsHtmlResponse ctx

        // Atomic cap check + create: counting and creating happen on one supervisor turn,
        // so two concurrent POSTs at the boundary can't both pass and exceed MaxGames.
        match supervisor.TryCreateGame(limits.MaxGames) with
        | None when wantsHtml ->
            // No-JS: redirect to the dashboard with a reason the refreshed page can surface.
            ctx.Response.StatusCode <- 303
            ctx.Response.Headers.Location <- "/?error=at-capacity"
        | None ->
            // Uniform interface: creation is not available at the game cap.
            ctx.Response.StatusCode <- 409
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync({| error = "MaxGamesReached" |})
        | Some(gameId, game) ->
            // Subscribe to game state changes
            subscribeToGame gameId game assignmentManager supervisor

            // Get initial state and broadcast to all clients
            use initialSub =
                game.Subscribe(
                    { new IObserver<MoveResult> with
                        member _.OnNext(result) =
                            let gameCount = supervisor.GetActiveGameCount()
                            let element = renderGameBoard gameId result "" None gameCount
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
                ctx.Response.Headers.Location <- $"/games/{gameId}"
    }

/// GET /games/{id} - Get a specific game
let getGame (ctx: HttpContext) =
    task {
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let gameId = ctx.Request.RouteValues.["id"] |> string

        // GET is safe: no subscription side effect here. Every game is subscribed for
        // broadcasts at creation (createGame / startup / reset), so a read need not register one.
        // Read from the supervisor's cached state (TryGetState): never messages the game actor,
        // so a just-deleted game returns None (404) instead of hanging on a Stopped actor.
        match supervisor.TryGetState(gameId) with
        | Some result ->
            let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
            let assignment = assignmentManager.GetAssignment(gameId)
            let gameCount = supervisor.GetActiveGameCount()
            ctx.Response.Headers.CacheControl <- "no-cache, private"
            ctx.Response.Headers.Vary <- "Cookie"
            let element = renderGameBoard gameId result userId assignment gameCount |> withBanner ctx |> layout.html ctx
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            do! Render.toStreamAsync ctx.Response.Body element
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
            | None -> ctx.Response.StatusCode <- 400
            | Some moveAction ->
                // Get current game state to determine whose turn it is
                let currentState = game.GetState()
                let xTurn = isXTurn currentState

                // Validate and potentially assign player
                let (validationResult, _) =
                    assignmentManager.TryAssignAndValidate(gameId, uid, xTurn)

                match validationResult with
                | Allowed ->
                    // Command accepted; the new board is projected via the SSE event stream.
                    subscribeToGame gameId game assignmentManager supervisor
                    game.MakeMove(moveAction)
                    if wantsHtml then
                        ctx.Response.StatusCode <- 303
                        ctx.Response.Headers.Location <- $"/games/{gameId}"
                    else
                        ctx.Response.StatusCode <- 202
                | Rejected reason ->
                    let slug = rejectionSlug reason
                    if wantsHtml then
                        // No-JS: redirect to the game carrying the reason, so the refreshed page
                        // can tell the player the move was rejected (not silently swallowed).
                        ctx.Response.StatusCode <- 303
                        ctx.Response.Headers.Location <- $"/games/{gameId}?error={slug}"
                    else
                        // Move was rejected - broadcast rejection animation
                        broadcast (PatchSignals $"""{{ "rejectionAnimation": "rejection-{slug}" }}""")
                        ctx.Response.StatusCode <- 403
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
                    subscribeToGame newGameId newGame assignmentManager supervisor

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
                                    let element = renderGameBoard newGameId result "" None gameCount
                                    broadcastToDashboard (PatchElementsAppend("#games-container", fun tw -> Render.toTextWriterAsync tw element))

                                member _.OnError(_) = ()
                                member _.OnCompleted() = () }
                        )

                    if wantsHtml then
                        // No-JS form: redirect to the freshly reset game.
                        ctx.Response.StatusCode <- 303
                        ctx.Response.Headers.Location <- $"/games/{newGameId}"
                    else
                        // Reset creates a new resource; 201 Created with its Location (200 + Location
                        // is undefined for a body-less response).
                        ctx.Response.StatusCode <- 201
                        ctx.Response.Headers.Location <- $"/games/{newGameId}"
            | _ ->
                ctx.Response.StatusCode <- 403  // Forbidden - not an assigned player
        | None, _ -> ctx.Response.StatusCode <- 404
        | _, None -> ctx.Response.StatusCode <- 401  // Unauthorized - no user
    }
