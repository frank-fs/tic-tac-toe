module TicTacToe.Web.templates.game

open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Model
open TicTacToe.Web.Surface

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
let renderErrorBanner (surface: Surface) (error: string) : HtmlElement =
    let message =
        match error with
        | "at-capacity" -> "Cannot create a new game: the game limit has been reached."
        | "not-your-turn" -> "Move rejected: it is not your turn."
        | "not-a-player" -> "Move rejected: you are not a player in this game."
        | "wrong-player" -> "Move rejected: wrong player."
        | "game-over" -> "Move rejected: the game is over."
        | _ -> "That action could not be completed."
    let banner = div(class' = "error-banner") { message }
    (if surface.C then banner.attr("role", "alert") else banner) :> HtmlElement

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

/// Occupancy of a square, in the vocabulary C announces to assistive tech.
let private occupancyOf (state: GameState) (position: SquarePosition) =
    match state.TryGetValue(position) with
    | true, Taken player -> player.ToString()
    | _ -> "empty"

/// C: an accessible cell announces its position and occupancy to assistive tech.
let private applyCell (surface: Surface) (state: GameState) (position: SquarePosition) (tag: HtmlTag) =
    if surface.C then
        tag.attr("role", "gridcell")
           .attr("aria-label", sprintf "%s, %s" (position.ToString()) (occupancyOf state position))
    else tag

/// The move form wrapping one square. Progressive enhancement: a real form so the move command
/// works as a plain POST without JavaScript; datastar enhances the submit (preventing the native
/// POST and sending the move as signals) when JS is present.
let private moveForm gameId (playerStr: string) (posStr: string) (square: HtmlElement) =
    form(method = "post", action = sprintf "/games/%s" gameId)
        .attr("rel", "make-move")
        .attr("data-on:submit__prevent", sprintf "$gameId = '%s'; $player = '%s'; $position = '%s'; @post('/games/%s')" gameId playerStr posStr gameId) {
        input(type' = "hidden", name = "player", value = playerStr)
        input(type' = "hidden", name = "position", value = posStr)
        square
    }
    :> HtmlElement

/// A submittable square. C=1 names the action ("Play X at TopLeft") and hides the decorative
/// preview glyph from the a11y tree.
let private submitSquare (surface: Surface) (state: GameState) (playerStr: string) (position: SquarePosition) =
    let btn =
        if surface.C then
            button(class' = "square square-clickable", type' = "submit")
                .attr("aria-label", sprintf "Play %s at %s" playerStr (position.ToString())) {
                span(class' = "preview").attr("aria-hidden", "true") { playerStr }
            }
        else
            button(class' = "square square-clickable", type' = "submit") {
                span(class' = "preview") { playerStr }
            }
    (applyCell surface state position btn) :> HtmlElement

/// A0's occupied / out-of-turn square: still inside a form, but the button carries HTML `disabled`
/// (verbatim from the twin). That is a BROWSER-only guard — an HTTP agent ignores it and sees nine
/// equally submittable forms, including the illegal ones. That is the point of the ungated design.
let private disabledSquare (surface: Surface) (state: GameState) (label: HtmlElement) (position: SquarePosition) =
    let btn = button(class' = "square", type' = "submit").attr("disabled", "disabled") { label }
    (applyCell surface state position btn) :> HtmlElement

/// A1's non-affordance cell: plain, non-interactive, no form.
let private renderPlainCell (surface: Surface) (state: GameState) (label: HtmlElement) (position: SquarePosition) =
    let btn = button(class' = "square", type' = "button").attr("disabled", "disabled") { label }
    (applyCell surface state position btn) :> HtmlElement

/// The glyph shown in a square that the caller cannot play into.
let private squareLabel (state: GameState) (position: SquarePosition) : HtmlElement =
    match state.TryGetValue(position) with
    | true, Taken player -> span(class' = "player") { player.ToString() } :> HtmlElement
    | _ -> span(class' = "empty") { raw "·" } :> HtmlElement

/// Render one square. A is affordance GATING, not presence (the banked Surface instrument):
///   A=0: ALL 9 squares are form-POST buttons (naive design); occupied/inactive ones are `disabled`
///        (browser-only — an HTTP agent sees nine equally submittable forms).
///   A=1: ONLY the caller's currently-legal moves are forms; every other square is a plain cell.
let private renderSquare
    (surface: Surface) (legal: Set<SquarePosition>) gameId (playerStr: string)
    (state: GameState) (isActive: bool) (position: SquarePosition) =
    let posStr = position.ToString()
    let isTaken = match state.TryGetValue(position) with | true, Taken _ -> true | _ -> false
    if surface.A then
        if Set.contains position legal then
            moveForm gameId playerStr posStr (submitSquare surface state playerStr position)
        else
            renderPlainCell surface state (squareLabel state position) position
    else
        let square =
            if isActive && not isTaken then submitSquare surface state playerStr position
            else disabledSquare surface state (squareLabel state position) position
        moveForm gameId playerStr posStr square

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
let private controlButton enabled (btnClass: string) (rel: string) (action: string) (datastarAction: string) (label: string) : HtmlElement =
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

/// Render control buttons (reset/delete) based on viewer assignment and game state.
/// A locked game (TICTACTOE_LOCK_GAME) offers neither, so an agent cannot reset-and-replay a run.
let private renderControls locked gameId viewerPlayer assignment gameCount activity =
    let resetEnabled, deleteEnabled =
        match viewerPlayer, assignment with
        | _ when locked -> (false, false)
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
/// Resolves the viewer's player token internally from assignment + userId — the self-seat: an
/// unseated visitor on X's turn sees the claimable board as X and seats X by submitting.
/// A is affordance GATING: A=1 forms only on the caller's legal squares; A=0 a form on all nine.
let renderGameBoard (surface: Surface) (gameId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (gameCount: int) : HtmlElement =
    let (State state) = result
    let viewerPlayer = resolveViewer assignment userId result
    let activity = hasGameActivity result assignment
    let locked = gameLocked ()
    let legal, currentPlayer, status, canMove =
        match (result, viewerPlayer) with
        | CanMove(player, validMoves, status) -> (Set.ofArray validMoves, Some player, status, true)
        | Watching(cp, status) -> (Set.empty, cp, status, false)
        | Finished status -> (Set.empty, None, status, false)
    // The player token the form submits: the viewer's own seat, else the seat the current turn
    // would claim (A=0's ungated squares still have to name a player).
    let playerStr =
        match viewerPlayer, currentPlayer with
        | Some p, _ -> p.ToString()
        | None, Some p -> p.ToString()
        | None, None -> "X"
    let isActive = match result with | XTurn _ | OTurn _ -> true | _ -> false
    let renderSquare = renderSquare surface legal gameId playerStr state isActive
    // Stable, machine-readable status token so a no-JS agent can decide turn/outcome without
    // parsing the display prose; data-can-move says whether THIS viewer may move now.
    let statusToken =
        match result with
        | XTurn _ -> "x-turn"
        | OTurn _ -> "o-turn"
        | Won(_, player) -> sprintf "won-%s" (player.ToString().ToLowerInvariant())
        | Draw _ -> "draw"
        | Error _ -> "error"
    let statusRegion =
        let d = div(class' = "status") { h2() { status } }
        if surface.C then d.attr("role", "status").attr("aria-live", "polite") else d
    let boardGrid =
        let d =
            div(class' = "board") {
                for position in allPositions do
                    renderSquare position
            }
        if surface.C then d.attr("role", "grid") else d
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
        // C: role="status" announces turn/win/draw to assistive tech on both the JS-morph and
        // the no-JS refresh paths; aria-live polite keeps the JS-morph announcement.
        statusRegion
        boardGrid
        renderLegend assignment currentPlayer
        renderControls locked gameId viewerPlayer assignment gameCount activity
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
