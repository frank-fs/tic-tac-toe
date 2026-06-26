module TicTacToe.Web.Surface.templates.game

open Oxpecker.ViewEngine
open Oxpecker.ViewEngine.Aria
open TicTacToe.Model
open TicTacToe.Web.Surface.Model
open TicTacToe.Web.Surface.Surface

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

/// The caller's seat in this arena, if any.
let private callerRole (assignment: PlayerAssignment option) (userId: string) =
    match assignment with
    | Some { PlayerXId = Some x } when x = userId -> Some X
    | Some { PlayerOId = Some o } when o = userId -> Some O
    | _ -> None

/// Squares the caller may legally move into right now (empty unless it is the
/// caller's turn). Reads the legal-move list the Engine already computed.
let private legalForCaller (result: MoveResult) (role: Player option) : Set<SquarePosition> =
    match result, role with
    | XTurn(_, vx), Some X -> vx |> Array.map (fun (XPos p) -> p) |> Set.ofArray
    | OTurn(_, vo), Some O -> vo |> Array.map (fun (OPos p) -> p) |> Set.ofArray
    | _ -> Set.empty

let private occupancyLabel (posStr: string) (isTaken: bool) (label: string) =
    if isTaken then $"{posStr}, {label}" else $"{posStr}, empty"

let private applyCell (surface: Surface) (posStr: string) (isTaken: bool) (label: string) (b: HtmlTag) =
    if surface.C then
        b.attr("role", "gridcell").attr("aria-label", occupancyLabel posStr isTaken label)
    else b

/// A1 non-affordance cell: plain, non-interactive.
let private renderPlainCell (surface: Surface) (posStr: string) (isTaken: bool) (label: string) =
    let b = button(class' = "square", type' = "button", ariaLabel = "").attr("disabled", "disabled") { label }
    applyCell surface posStr isTaken label b :> HtmlElement

/// Render a single square.
/// A0: All 9 squares rendered as form-POST buttons (naive design); occupied/inactive disabled.
/// A1: Only the caller's currently-legal moves rendered as forms; all others are plain cells.
let private renderSquare (surface: Surface) (legal: Set<SquarePosition>) (arenaId: string) (playerStr: string) (state: GameState) (isActive: bool) (position: SquarePosition) =
    let posStr = position.ToString()
    let isTaken, label =
        match state.TryGetValue(position) with
        | true, Taken X -> true, "X"
        | true, Taken O -> true, "O"
        | _ -> false, "·"
    if surface.A then
        if Set.contains position legal then
            let btn = applyCell surface posStr isTaken label (button (class' = "square square-clickable", type' = "submit", ariaLabel = posStr) { label })
            form (method = "post", action = $"/arenas/{arenaId}") {
                input (type' = "hidden", name = "player", value = playerStr)
                input (type' = "hidden", name = "position", value = posStr)
                btn
            } :> HtmlElement
        else
            renderPlainCell surface posStr isTaken label
    else
        let clickable = isActive && not isTaken
        let rawSquare =
            if clickable then
                button (class' = "square square-clickable", type' = "submit", ariaLabel = posStr) { label }
            else
                button(class' = "square", type' = "submit", ariaLabel = posStr).attr("disabled", "disabled") { label }
        let square = applyCell surface posStr isTaken label rawSquare
        form (method = "post", action = $"/arenas/{arenaId}") {
            input (type' = "hidden", name = "player", value = playerStr)
            input (type' = "hidden", name = "position", value = posStr)
            square
        } :> HtmlElement

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
            button (class' = "reset-game-btn", type' = "submit") { "Restart Game" }
        }
        form (method = "post", action = $"/arenas/{arenaId}/delete") {
            button (class' = "delete-game-btn", type' = "submit") { "Delete Game" }
        }
    }

/// Domain ontology block, trimmed to what a cold-start LLM can actually use.
let private renderOntology () =
    script (type' = "application/ld+json") {
        raw """{
  "@context": "https://schema.org",
  "@type": "Game",
  "name": "Tic-tac-toe",
  "description": "An m,n,k-game with parameters (3,3,3): 3x3 board, 3-in-a-row to win.",
  "genre": "abstract strategy",
  "isBasedOn": [
    { "@type": "Game", "name": "Connect Four", "description": "m,n,k = (7,6,4)" },
    { "@type": "Game", "name": "Gomoku", "description": "m,n,k = (15,15,5)" }
  ],
  "strategy": "Solved: with perfect play tic-tac-toe is a draw; optimal play never loses."
}"""
    }
    :> HtmlElement

/// Render a complete arena page.
/// errorMsg — optional inline error (wrong player, game over, etc.)
let renderArenaPage (surface: Surface) (arenaId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (errorMsg: string option) =
    let (State state) = result
    let status = statusText result
    let active = isInProgress result
    let playerStr = resolvePlayerStr assignment userId result
    let role = callerRole assignment userId
    let legal = legalForCaller result role
    let board =
        let d = div (class' = "board") {
            for position in allPositions do
                renderSquare surface legal arenaId playerStr state active position
        }
        if surface.C then d.attr("role", "grid") else d
    let statusDiv =
        let d = div (class' = "status") { status }
        if surface.C then d.attr("aria-live", "polite") else d
    let backLink : HtmlElement =
        if surface.C then
            nav () { a (class' = "back-link", href = "/") { "Back to game list" } }
        else
            a (class' = "back-link", href = "/") { "Back to game list" }

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

        if surface.So then renderOntology () else Fragment() { }

        div (class' = "arena-header") {
            h1 () { "Tic Tac Toe" }
            p () { $"Game: {arenaId.[..7]}" }
        }

        match errorMsg with
        | Some msg ->
            div (class' = "error-msg") { msg }
        | None -> ()

        board
        statusDiv
        renderLegend assignment result
        renderControls arenaId
        backLink
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
