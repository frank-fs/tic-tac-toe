module TicTacToe.Web.Simple.templates.game

open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Simple.Model

#nowarn "3391"

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
// Utilities
// ============================================================================

let private (|State|) = function
    | XTurn(s, _) | OTurn(s, _) | Won(s, _) | Draw s | Error(s, _) -> s

/// Display first 8 characters of a user ID, or a placeholder if not assigned
let shortUserId (id: string option) (placeholder: string) =
    id |> Option.map (fun s -> s.[..7]) |> Option.defaultValue placeholder

/// Derive the "player" string to embed in form hidden field for a given userId
let private resolvePlayerStr (assignment: PlayerAssignment option) (userId: string) (result: MoveResult) =
    match assignment with
    | Some { PlayerXId = Some xId } when xId = userId -> "X"
    | Some { PlayerOId = Some oId } when oId = userId -> "O"
    | Some { PlayerXId = Some _; PlayerOId = Some _ } ->
        // Both slots taken, user is a spectator — default to X so form still renders
        "X"
    | _ ->
        // Auto-assign: if X's turn assign X, if O's turn assign O
        match result with
        | XTurn _ -> "X"
        | OTurn _ -> "O"
        | _ -> "X"

/// Status text for the game
let private statusText = function
    | XTurn _ -> "X's turn"
    | OTurn _ -> "O's turn"
    | Won(_, player) -> $"{player} wins!"
    | Draw _ -> "It's a draw!"
    | Error(_, msg) -> $"Error: {msg}"

/// Whether the game is still in progress
let private isInProgress = function
    | XTurn _ | OTurn _ -> true
    | _ -> false

// ============================================================================
// Rendering
// ============================================================================

/// Render a single square as a form-POST button.
/// ALL 9 squares are always shown as clickable buttons regardless of game state.
let private renderSquare (arenaId: string) (playerStr: string) (state: GameState) (isActive: bool) (position: SquarePosition) =
    let posStr = position.ToString()
    let label =
        match state.TryGetValue(position) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "·"
    form (method = "post", action = $"/arenas/{arenaId}") {
        input (type' = "hidden", name = "player", value = playerStr)
        input (type' = "hidden", name = "position", value = posStr)
        button (
            class' = (if isActive then "square square-clickable" else "square"),
            type' = "submit"
        ) { label }
    }
    :> HtmlElement

/// Render the player legend
let private renderLegend (assignment: PlayerAssignment option) (result: MoveResult) =
    let currentPlayer =
        match result with
        | XTurn _ -> Some X
        | OTurn _ -> Some O
        | _ -> None
    let xLabel =
        assignment |> Option.bind (fun a -> a.PlayerXId) |> fun id -> shortUserId id "Waiting..."
    let oLabel =
        assignment |> Option.bind (fun a -> a.PlayerOId) |> fun id -> shortUserId id "Waiting..."
    let legendClass player =
        match currentPlayer with
        | Some p when p = player -> "legend-active"
        | _ -> ""
    div (class' = "legend") {
        span (class' = legendClass X) { $"X: {xLabel}" }
        span (class' = legendClass O) { $"O: {oLabel}" }
    }

/// Render control buttons (reset / delete)
let private renderControls (arenaId: string) =
    div (class' = "controls") {
        form (method = "post", action = $"/arenas/{arenaId}/restart") {
            button (class' = "reset-game-btn", type' = "submit") { "Restart Arena" }
        }
        form (method = "post", action = $"/arenas/{arenaId}/delete") {
            button (class' = "delete-game-btn", type' = "submit") { "Delete Arena" }
        }
    }

/// Render a complete arena page.
/// errorMsg — optional inline error (wrong player, game over, etc.)
let renderArenaPage (arenaId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (errorMsg: string option) =
    let (State state) = result
    let status = statusText result
    let active = isInProgress result
    let playerStr = resolvePlayerStr assignment userId result

    Fragment() {
        style () {
            raw
                """
            body { font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; }
            .arena-header { text-align: center; margin-bottom: 16px; }
            .status { text-align: center; font-size: 1.4em; font-weight: bold; margin-bottom: 12px; color: #333; }
            .error-msg { text-align: center; color: #d32f2f; background: #ffebee; border-radius: 4px; padding: 8px; margin-bottom: 12px; }
            .board { display: grid; grid-template-columns: repeat(3, 68px); gap: 4px; margin: 0 auto 16px auto; width: fit-content; background: #333; padding: 4px; }
            .board form { margin: 0; padding: 0; }
            .square { width: 60px; height: 60px; background: #fff; border: none; display: flex; align-items: center; justify-content: center; font-size: 1.6em; font-weight: bold; cursor: default; }
            .square-clickable { cursor: pointer; background: #f0f8ff; }
            .square-clickable:hover { background: #dbeeff; }
            .legend { display: flex; justify-content: center; gap: 16px; margin: 8px 0; font-size: 0.9em; color: #555; }
            .legend-active { font-weight: bold; color: #111; }
            .controls { text-align: center; margin-top: 12px; }
            .reset-game-btn { background: #2196F3; color: white; padding: 8px 16px; font-size: 12px; border: none; border-radius: 4px; cursor: pointer; margin-right: 8px; }
            .reset-game-btn:hover { background: #1976D2; }
            .delete-game-btn { background: #f44336; color: white; padding: 8px 16px; font-size: 12px; border: none; border-radius: 4px; cursor: pointer; }
            .delete-game-btn:hover { background: #d32f2f; }
            .back-link { display: block; text-align: center; margin-top: 16px; color: #555; text-decoration: none; }
            .back-link:hover { text-decoration: underline; }
            .page-header { display: flex; justify-content: flex-end; padding: 8px 20px; }
            .user-identity { font-family: monospace; font-size: 0.85em; color: #666; }
            """
        }

        div (class' = "arena-header") {
            h1 () { "Tic Tac Toe" }
            p () { $"Arena: {arenaId.[..7]}" }
        }

        div (class' = "status") { status }

        match errorMsg with
        | Some msg ->
            div (class' = "error-msg") { msg }
        | None -> ()

        div (class' = "board") {
            for position in allPositions do
                renderSquare arenaId playerStr state active position
        }

        renderLegend assignment result
        renderControls arenaId

        a (class' = "back-link", href = "/") { "Back to arena list" }
    }

/// CSS styles for the home page (arena list)
let homeStyles =
    style () {
        raw
            """
        body { font-family: Arial, sans-serif; max-width: 900px; margin: 0 auto; padding: 20px; }
        .title { text-align: center; font-size: 2em; margin-bottom: 20px; color: #333; }
        .new-arena-container { text-align: center; margin-bottom: 20px; }
        .new-arena-btn { background: #4CAF50; color: white; padding: 12px 24px; font-size: 16px; border: none; border-radius: 4px; cursor: pointer; }
        .new-arena-btn:hover { background: #45a049; }
        .arenas-list { list-style: none; padding: 0; }
        .arena-item { background: #f5f5f5; border-radius: 8px; padding: 12px 16px; margin-bottom: 8px; display: flex; justify-content: space-between; align-items: center; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
        .arena-link { color: #1976D2; font-family: monospace; text-decoration: none; font-size: 0.95em; }
        .arena-link:hover { text-decoration: underline; }
        .arena-status { font-size: 0.9em; color: #555; }
        .no-arenas { text-align: center; color: #888; font-style: italic; padding: 24px; }
        .page-header { display: flex; justify-content: flex-end; padding: 8px 20px; }
        .user-identity { font-family: monospace; font-size: 0.85em; color: #666; }
        """
    }
