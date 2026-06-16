module TicTacToe.Web.templates.game

open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Model

let private allPositions =
    [ TopLeft
      TopCenter
      TopRight
      MiddleLeft
      MiddleCenter
      MiddleRight
      BottomLeft
      BottomCenter
      BottomRight ]

// ============================================================================
// Active Patterns
// ============================================================================

/// Extract game state from any MoveResult
let private (|State|) = function
    | XTurn(s, _) | OTurn(s, _) | Won(s, _) | Draw s | Error(s, _) -> s

/// Resolve the viewer's player token from game context.
/// Returns Some X/O if viewer can act as that player, None for spectators.
let private resolveViewer (assignment: PlayerAssignment option) (userId: string) (result: MoveResult) =
    match assignment with
    | Some { PlayerXId = Some xId } when xId = userId -> Some X
    | Some { PlayerOId = Some oId } when oId = userId -> Some O
    | Some { PlayerXId = Some _; PlayerOId = Some _ } -> None
    | _ ->
        match result with
        | XTurn _ -> Some X
        | OTurn _ -> Some O
        | _ -> None

/// Decompose (MoveResult, viewerPlayer) into rendering modes.
/// CanMove: viewer is the active player — show clickable valid-move squares.
/// Watching: game in progress but not viewer's turn — static board.
/// Finished: game is over — static board.
let private (|CanMove|Watching|Finished|) = function
    | XTurn(_, moves), Some X -> CanMove(X, moves |> Array.map (fun (XPos pos) -> pos), "X's turn")
    | OTurn(_, moves), Some O -> CanMove(O, moves |> Array.map (fun (OPos pos) -> pos), "O's turn")
    | XTurn _, _               -> Watching(Some X, "X's turn")
    | OTurn _, _               -> Watching(Some O, "O's turn")
    | Won(_, player), _        -> Finished $"{player} wins!"
    | Draw _, _                -> Finished "It's a draw!"
    | Error(_, msg), _         -> Finished $"Error: {msg}"

// ============================================================================
// Public Utilities
// ============================================================================

/// First 8 characters of an id (or fewer if it is shorter — ids are GUIDs in practice,
/// but never assume the length).
let private prefix8 (s: string) =
    if s.Length > 8 then s.Substring(0, 8) else s

/// Display first 8 characters of a user ID, or a placeholder if not assigned
let shortUserId (id: string option) (placeholder: string) =
    id |> Option.map prefix8 |> Option.defaultValue placeholder

/// Check if game has activity (moves made or players assigned)
let hasGameActivity (result: MoveResult) (assignment: PlayerAssignment option) =
    match result with
    | Won _ | Draw _ | Error _ -> true
    | XTurn(state, _) | OTurn(state, _) ->
        let hasMoves = state.Values |> Seq.exists (function Taken _ -> true | _ -> false)
        let hasPlayers =
            match assignment with
            | Some { PlayerXId = Some _ } | Some { PlayerOId = Some _ } -> true
            | _ -> false
        hasMoves || hasPlayers

/// No-JS error banner rendered from a Post/Redirect/Get ?error= flash token, so a rejected
/// write (at-capacity create, rejected move) is legible after the redirect without JS.
let renderErrorBanner (error: string) : HtmlElement =
    let message =
        match error with
        | "at-capacity" -> "Cannot create a new game: the game limit has been reached."
        | "not-your-turn" -> "Move rejected: it is not your turn."
        | "not-a-player" -> "Move rejected: you are not a player in this game."
        | "wrong-player" -> "Move rejected: wrong player."
        | "game-over" -> "Move rejected: the game is over."
        | _ -> "That action could not be completed."
    div(class' = "error-banner").attr("role", "alert") { message }
    :> HtmlElement

/// Self-descriptive 404 body for a missing game, with a way home.
let notFoundPage: HtmlElement =
    div(class' = "game-info") {
        p() {
            raw "Game not found. "
            a(href = "/") { "Back to games" }
        }
    }
    :> HtmlElement

// ============================================================================
// Private Rendering
// ============================================================================

/// Render a clickable square for a valid move.
/// Progressive enhancement: a real form so the move command works as a plain POST
/// without JavaScript; datastar enhances the submit (preventing the native POST and
/// sending the move as signals) when JS is present.
let private renderClickableSquare gameId (player: Player) (position: SquarePosition) =
    let posStr = position.ToString()
    let playerStr = player.ToString()
    form(method = "post", action = sprintf "/games/%s" gameId)
        .attr("rel", "make-move")
        .attr("data-on:submit__prevent", sprintf "$gameId = '%s'; $player = '%s'; $position = '%s'; @post('/games/%s')" gameId playerStr posStr gameId) {
        input(type' = "hidden", name = "player", value = playerStr)
        input(type' = "hidden", name = "position", value = posStr)
        button(class' = "square square-clickable", type' = "submit")
            .attr("aria-label", sprintf "Play %s at %s" playerStr posStr) {
            span(class' = "preview").attr("aria-hidden", "true") { playerStr }
        }
    }
    :> HtmlElement

/// Render a static square (taken or empty)
let private renderStaticSquare (state: GameState) (position: SquarePosition) =
    let content =
        match state.TryGetValue(position) with
        | true, Taken player -> span(class' = "player") { player.ToString() }
        | _ -> span(class' = "empty") { raw "·" }
    div(class' = "square") { content }
    :> HtmlElement

/// Render the player legend showing X and O assignments
let private renderLegend (assignment: PlayerAssignment option) (currentPlayer: Player option) =
    let xLabel =
        assignment |> Option.bind (fun a -> a.PlayerXId) |> fun id -> shortUserId id "Waiting for player..."
    let oLabel =
        assignment |> Option.bind (fun a -> a.PlayerOId) |> fun id -> shortUserId id "Waiting for player..."
    let legendClass player =
        match currentPlayer with
        | Some p when p = player -> "legend-active"
        | _ -> ""
    div(class' = "legend") {
        span(class' = legendClass X) { $"X: {xLabel}" }
        span(class' = legendClass O) { $"O: {oLabel}" }
    }

/// Render one control as a real no-JS form (enabled) or a disabled button (disabled).
/// rel types the affordance in the markup (the delete form is the no-JS POST alias for the
/// DELETE verb on the canonical /games/{id} resource). datastarAction enhances the submit.
let private controlButton enabled (btnClass: string) (rel: string) (action: string) (datastarAction: string) (label: string) =
    if enabled then
        form(method = "post", action = action)
            .attr("rel", rel)
            .attr("data-on:submit__prevent", datastarAction) {
            button(class' = btnClass, type' = "submit") { label }
        }
        :> HtmlElement
    else
        button(class' = btnClass, type' = "button").attr("disabled", "disabled") { label }
        :> HtmlElement

/// Render control buttons (reset/delete) based on viewer assignment and game state
let private renderControls gameId viewerPlayer assignment gameCount activity =
    let resetEnabled, deleteEnabled =
        match viewerPlayer, assignment with
        | Some X, Some { PlayerXId = Some _ }
        | Some O, Some { PlayerOId = Some _ } ->
            (activity, true)
        | _ ->
            // For unassigned games/spectators: only enable if there's activity AND gameCount > 6
            (activity && gameCount > 6, gameCount > 6)
    div(class' = "controls") {
        // Real forms so reset/delete work with no JS; datastar enhances the submit when
        // present (reset POSTs; delete uses the DELETE verb via @delete). HTML forms cannot
        // emit DELETE, so the no-JS path posts to /games/{id}/delete.
        controlButton resetEnabled "reset-game-btn" "reset-game"
            (sprintf "/games/%s/reset" gameId) (sprintf "@post('/games/%s/reset')" gameId) "Reset Game"
        controlButton deleteEnabled "delete-game-btn" "delete-game"
            (sprintf "/games/%s/delete" gameId) (sprintf "@delete('/games/%s')" gameId) "Delete Game"
    }

// ============================================================================
// Main Render Function
// ============================================================================

/// Render a complete game board, personalized for the given viewer.
/// Resolves the viewer's player token internally from assignment + userId.
let renderGameBoard (gameId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (gameCount: int) : HtmlElement =
    let (State state) = result
    let viewerPlayer = resolveViewer assignment userId result
    let activity = hasGameActivity result assignment
    let renderSquare, currentPlayer, status, canMove =
        match (result, viewerPlayer) with
        | CanMove(player, validMoves, status) ->
            let render pos =
                if validMoves |> Array.contains pos then
                    renderClickableSquare gameId player pos
                else
                    renderStaticSquare state pos
            (render, Some player, status, true)
        | Watching(cp, status) ->
            (renderStaticSquare state, cp, status, false)
        | Finished status ->
            (renderStaticSquare state, None, status, false)
    // Stable, machine-readable status token so a no-JS agent can decide turn/outcome without
    // parsing the display prose; data-can-move says whether THIS viewer may move now.
    let statusToken =
        match result with
        | XTurn _ -> "x-turn"
        | OTurn _ -> "o-turn"
        | Won(_, player) -> sprintf "won-%s" (player.ToString().ToLowerInvariant())
        | Draw _ -> "draw"
        | Error _ -> "error"
    div(id = $"game-{gameId}", class' = "game-board")
        .attr("data-game-status", statusToken)
        .attr("data-can-move", (if canMove then "true" else "false"))
        .attr("data-signals", sprintf "{gameId: '%s', player: '', position: ''}" gameId) {
        // Canonical link + full id as text so the / -> /games/{id} trail is navigable without
        // JS and an agent can transcribe the id from the link text (a truncated label would
        // not be navigable).
        div(class' = "game-link") {
            a(href = sprintf "/games/%s" gameId) { sprintf "Game %s" gameId }
        }
        // role="status" announces turn/win/draw to assistive tech on both the JS-morph and
        // the no-JS refresh paths; aria-live polite keeps the JS-morph announcement.
        div(class' = "status").attr("role", "status").attr("aria-live", "polite") { h2() { status } }
        div(class' = "board") {
            for position in allPositions do
                renderSquare position
        }
        renderLegend assignment currentPlayer
        renderControls gameId viewerPlayer assignment gameCount activity
    }

/// CSS styles for the game board
let gameStyles =
    style() {
        raw
            """
        .game-container {
            max-width: 800px;
            margin: 0 auto;
            padding: 20px;
            font-family: Arial, sans-serif;
        }

        .title {
            text-align: center;
            font-size: 2em;
            margin-bottom: 20px;
            color: #333;
        }

        .new-game-container {
            text-align: center;
            margin-bottom: 20px;
        }

        .games-container {
            display: flex;
            flex-wrap: wrap;
            gap: 20px;
            justify-content: center;
        }

        .game-board {
            background-color: #f5f5f5;
            border-radius: 8px;
            padding: 15px;
            box-shadow: 0 2px 4px rgba(0,0,0,0.1);
        }

        .status {
            text-align: center;
            margin-bottom: 15px;
        }

        .status h2 {
            font-size: 1.2em;
            color: #555;
            margin: 0;
        }

        .board {
            display: grid;
            grid-template-columns: repeat(3, 1fr);
            grid-gap: 4px;
            max-width: 200px;
            margin: 0 auto 15px auto;
            background-color: #333;
            padding: 4px;
        }

        /* Move forms wrap each clickable square; let the button be the grid item. */
        .board form { display: contents; }

        .square {
            width: 60px;
            height: 60px;
            background-color: #fff;
            border: none;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 1.5em;
            font-weight: bold;
            cursor: default;
        }

        .square-clickable {
            cursor: pointer;
            background-color: #f0f8ff;
            transition: background-color 0.2s;
        }

        .square-clickable:hover {
            background-color: #e6f3ff;
        }

        .square .player {
            color: #333;
        }

        .square .preview {
            color: #999;
        }

        .square .empty {
            color: #ccc;
            font-size: 1em;
        }

        .legend {
            display: flex;
            justify-content: center;
            gap: 16px;
            margin: 8px 0;
            font-size: 0.9em;
            color: #555;
        }

        .legend-active {
            font-weight: bold;
        }

        .controls {
            text-align: center;
        }

        /* Reset/Delete are real forms now; keep them inline like the old buttons. */
        .controls form {
            display: inline;
        }

        .game-link {
            text-align: center;
            margin-bottom: 8px;
            font-size: 0.85em;
        }

        .error-banner {
            max-width: 800px;
            margin: 12px auto;
            padding: 12px 16px;
            background-color: #fdecea;
            border: 1px solid #f5c6cb;
            border-radius: 4px;
            color: #842029;
            text-align: center;
        }

        .new-game-btn {
            background-color: #4CAF50;
            color: white;
            padding: 12px 24px;
            font-size: 16px;
            border: none;
            border-radius: 4px;
            cursor: pointer;
            transition: background-color 0.2s;
        }

        .new-game-btn:hover {
            background-color: #45a049;
        }

        .reset-game-btn {
            background-color: #2196F3;
            color: white;
            padding: 8px 16px;
            font-size: 12px;
            border: none;
            border-radius: 4px;
            cursor: pointer;
            transition: background-color 0.2s;
            margin-right: 8px;
        }

        .reset-game-btn:hover:not(:disabled) {
            background-color: #1976D2;
        }

        .reset-game-btn:disabled {
            background-color: #90CAF9;
            cursor: not-allowed;
            opacity: 0.6;
        }

        .delete-game-btn {
            background-color: #f44336;
            color: white;
            padding: 8px 16px;
            font-size: 12px;
            border: none;
            border-radius: 4px;
            cursor: pointer;
            transition: background-color 0.2s;
        }

        .delete-game-btn:hover:not(:disabled) {
            background-color: #d32f2f;
        }

        .delete-game-btn:disabled {
            background-color: #EF9A9A;
            cursor: not-allowed;
            opacity: 0.6;
        }

        .loading {
            text-align: center;
            color: #666;
            font-style: italic;
            padding: 40px;
        }

        .game-info {
            text-align: center;
            margin-top: 20px;
            color: #666;
        }

        .page-header {
            display: flex;
            justify-content: flex-end;
            padding: 8px 20px;
        }

        .user-identity {
            font-family: monospace;
            font-size: 0.85em;
            color: #666;
            overflow: hidden;
            text-overflow: ellipsis;
            max-width: 120px;
        }
        """
    }
