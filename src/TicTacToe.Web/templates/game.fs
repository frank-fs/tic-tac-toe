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

/// The 3 rows a real tic-tac-toe board has, in allPositions' existing fixed order (Top* then
/// Middle* then Bottom*) -- needed for the ARIA grid pattern's required role="row" grouping.
let private boardRows = [ allPositions.[0..2]; allPositions.[3..5]; allPositions.[6..8] ]

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
        | "position-taken" -> "That square is already taken."
        | "invalid-move" -> "Invalid move format."
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

/// One resource, two names: /games is the product route, /arenas the banked experiment route.
let aliasOf (basePath: string) =
    if basePath = "/arenas" then "/games" else "/arenas"

/// Natural-language position name, for accessible labels only -- the wire-format position
/// value the move form submits always stays SquarePosition.ToString()'s spelling ("TopLeft"
/// etc, the protocol vocabulary Mcp.fs's tool description also uses), completely unchanged;
/// this is prose meant to be heard, never parsed.
let private humanPosition (position: SquarePosition) : string =
    match position with
    | TopLeft -> "top left" | TopCenter -> "top center" | TopRight -> "top right"
    | MiddleLeft -> "middle left" | MiddleCenter -> "middle center" | MiddleRight -> "middle right"
    | BottomLeft -> "bottom left" | BottomCenter -> "bottom center" | BottomRight -> "bottom right"

/// Occupancy, in the prose C announces to assistive tech: what a screen-reader user needs to
/// know about a square isn't just "X" -- it's that the square is CLAIMED BY X, versus empty
/// and (elsewhere) actionable. Distinct from the machine-readable token elsewhere in this file
/// (statusToken, occupancyOf-shaped values other code may still want the bare "X"/"O"/"empty" for).
let private occupancyPhrase (state: GameState) (position: SquarePosition) =
    match state.TryGetValue(position) with
    | true, Taken player -> sprintf "claimed by %s" (player.ToString())
    | _ -> "empty"

/// C: mark a square as a grid cell for assistive tech. The accessible NAME (aria-label) is set
/// by each specific renderer below (submitSquare/disabledSquare/renderPlainCell), never here --
/// each needs different phrasing (actionable vs not), and setting a second, generic aria-label
/// on top of an already-labeled button used to silently produce a duplicate attribute (HTML
/// keeps only the first; the second, more informative one was always dead on arrival).
let private applyGridCellRole (surface: Surface) (tag: HtmlTag) =
    if surface.C then tag.attr("role", "gridcell") else tag

/// The move form wrapping one square. Progressive enhancement: a real form so the move command
/// works as a plain POST without JavaScript; datastar enhances the submit (preventing the native
/// POST and sending the move as signals) when JS is present.
let private moveForm (basePath: string) gameId (playerStr: string) (posStr: string) (square: HtmlElement) =
    form(method = "post", action = sprintf "%s/%s" basePath gameId)
        .attr("rel", "make-move")
        .attr("data-on:submit__prevent", sprintf "$gameId = '%s'; $player = '%s'; $position = '%s'; @post('%s/%s')" gameId playerStr posStr basePath gameId) {
        input(type' = "hidden", name = "player", value = playerStr)
        input(type' = "hidden", name = "position", value = posStr)
        square
    }
    :> HtmlElement

/// A submittable, empty square: the label names the LOCATION, its occupancy, and the claim
/// action in one phrase ("top left square, empty, claim it for X") -- a screen-reader user
/// hears what the square IS and what it does, not just a bare button name. Hides the decorative
/// X/O preview glyph from the a11y tree (its meaning is already in the label).
let private submitSquare (surface: Surface) (state: GameState) (playerStr: string) (position: SquarePosition) =
    let btn =
        if surface.C then
            button(class' = "square square-clickable", type' = "submit")
                .attr("aria-label", sprintf "%s square, empty, claim it for %s" (humanPosition position) playerStr) {
                span(class' = "preview").attr("aria-hidden", "true") { playerStr }
            }
        else
            button(class' = "square square-clickable", type' = "submit") {
                span(class' = "preview") { playerStr }
            }
    (applyGridCellRole surface btn) :> HtmlElement

/// A0's occupied / out-of-turn square: still a real, live form -- no HTML `disabled`. The native
/// `disabled` attribute was here before (a BROWSER-only guard, ignored by an HTTP agent), but it
/// also silently removes an element from the accessibility tree and tab order regardless of
/// whatever role/aria-label also sits on it -- confirmed live (Chrome), on a fresh page load,
/// nothing SSE/morph-related about it. `disabled` was never the real legality boundary anyway
/// (the server validates and rejects an illegal move independent of it); removing it makes a
/// browser click behave the same way an HTTP agent's POST already does -- submit, then a real
/// server-side accept/reject -- and makes A=0 genuinely ungated for every client, not just
/// HTTP ones. The label states the real location and occupancy either way -- a non-actionable
/// square is still a real place on the board worth knowing about.
let private disabledSquare (surface: Surface) (state: GameState) (label: HtmlElement) (position: SquarePosition) =
    let btn = button(class' = "square", type' = "submit") { label }
    let btn = if surface.C then btn.attr("aria-label", sprintf "%s square, %s" (humanPosition position) (occupancyPhrase state position)) else btn
    (applyGridCellRole surface btn) :> HtmlElement

/// A1's non-affordance cell: plain, non-interactive, no form (still true without `disabled` --
/// type="button" outside any form was never submittable regardless of that attribute; removing
/// it here is a pure accessibility-tree-exposure fix, no behavior change). Same location+
/// occupancy label as disabledSquare -- a non-legal square is still a real place on the board
/// to know about.
let private renderPlainCell (surface: Surface) (state: GameState) (label: HtmlElement) (position: SquarePosition) =
    let btn = button(class' = "square", type' = "button") { label }
    let btn = if surface.C then btn.attr("aria-label", sprintf "%s square, %s" (humanPosition position) (occupancyPhrase state position)) else btn
    (applyGridCellRole surface btn) :> HtmlElement

/// The glyph shown in a square that the caller cannot play into.
let private squareLabel (state: GameState) (position: SquarePosition) : HtmlElement =
    match state.TryGetValue(position) with
    | true, Taken player -> span(class' = "player") { player.ToString() } :> HtmlElement
    | _ -> span(class' = "empty") { raw "·" } :> HtmlElement

/// Render one square. A is affordance GATING, not presence (the banked Surface instrument):
///   A=0: ALL 9 squares are form-POST buttons (naive design), every one genuinely submittable --
///        occupied/inactive ones aren't client-side disabled; the server rejects an illegal move
///        the same way for a browser click as it already does for an HTTP agent's raw POST.
///   A=1: ONLY the caller's currently-legal moves are forms; every other square is a plain cell.
let private renderSquare
    (surface: Surface) (legal: Set<SquarePosition>) (basePath: string) gameId (playerStr: string)
    (state: GameState) (isActive: bool) (position: SquarePosition) =
    let posStr = position.ToString()
    let isTaken = match state.TryGetValue(position) with | true, Taken _ -> true | _ -> false
    if surface.A then
        if Set.contains position legal then
            moveForm basePath gameId playerStr posStr (submitSquare surface state playerStr position)
        else
            renderPlainCell surface state (squareLabel state position) position
    else
        let square =
            if isActive && not isTaken then submitSquare surface state playerStr position
            else disabledSquare surface state (squareLabel state position) position
        moveForm basePath gameId playerStr posStr square

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

/// One control, as a real no-JS form. rel types the affordance in the markup (the delete form is
/// the no-JS POST alias for the DELETE verb on the canonical resource); datastarAction enhances
/// the submit when JS is present. C: a11yLabel names WHICH game this control acts on -- in a
/// multi-game dashboard a screen reader's button list shows "Reset Game" x N indistinguishably
/// without it.
let private controlForm (surface: Surface) (btnClass: string) (rel: string) (action: string) (datastarAction: string) (label: string) (a11yLabel: string) : HtmlElement =
    form(method = "post", action = action)
        .attr("rel", rel)
        .attr("data-on:submit__prevent", datastarAction) {
        let btn = button(class' = btnClass, type' = "submit") { label }
        if surface.C then btn.attr("aria-label", a11yLabel) else btn
    }
    :> HtmlElement

/// Reset/delete controls, verbatim from the twin: BOTH are always real forms while the game is in
/// progress — no viewer/seat/count/lock gating in the markup. Authorization is the HANDLER's job
/// (403 not-a-player, 409 locked / would-drop-below-minimum). Gating them here would change the
/// form count an agent sees, which is the surface the banked results were produced against.
let private renderControls (surface: Surface) (basePath: string) gameId =
    let shortId = prefix8 gameId
    div(class' = "controls") {
        controlForm surface "reset-game-btn" "reset-game"
            (sprintf "%s/%s/reset" basePath gameId) (sprintf "@post('%s/%s/reset')" basePath gameId)
            "Reset Game" (sprintf "Reset game %s" shortId)
        controlForm surface "delete-game-btn" "delete-game"
            (sprintf "%s/%s/delete" basePath gameId) (sprintf "@delete('%s/%s')" basePath gameId)
            "Delete Game" (sprintf "Delete game %s" shortId)
    }

// ============================================================================
// Main Render Function
// ============================================================================

/// Render a complete game board, personalized for the given viewer.
/// Resolves the viewer's player token internally from assignment + userId — the self-seat: an
/// unseated visitor on X's turn sees the claimable board as X and seats X by submitting.
/// A is affordance GATING: A=1 forms only on the caller's legal squares; A=0 a form on all nine.
let renderGameBoard (surface: Surface) (basePath: string) (gameId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (gameCount: int) : HtmlElement =
    let (State state) = result
    let viewerPlayer = resolveViewer assignment userId result
    let activity = hasGameActivity result assignment
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
    let renderSquare = renderSquare surface legal basePath gameId playerStr state isActive
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
        // aria-atomic: the whole region re-reads on change, not just whatever an AT implementation
        // decides is the "changed part" of a datastar-morphed live region -- needed because this
        // status text (not just one word) is what says whose turn it is.
        let d = div(class' = "status") { h2() { status } }
        if surface.C then d.attr("role", "status").attr("aria-live", "polite").attr("aria-atomic", "true") else d
    // Grid > row > gridcell: a bare role="grid" with role="gridcell" children and no row grouping
    // is an incomplete ARIA grid per the APG pattern (axe: aria-required-children/-parent) --
    // screen readers can't announce cell position or navigate the grid correctly without it.
    // display:contents (gameStyles below) keeps the CSS grid layout unaffected by the wrapper.
    let boardGrid =
        let rowOf (positions: SquarePosition list) =
            let r = div(class' = "board-row") { for position in positions do renderSquare position }
            if surface.C then r.attr("role", "row") else r
        let d = div(class' = "board") { for row in boardRows do rowOf row }
        if surface.C then d.attr("role", "grid").attr("aria-label", "Tic-tac-toe board") else d
    let aliasLink =
        let a' = a(class' = "game-alias", rel = "alternate", href = sprintf "%s/%s" (aliasOf basePath) gameId) { "alias" }
        // WCAG 2.5.3 Label in Name: the accessible name must contain the visible text ("alias")
        // verbatim, so voice-control users saying what they see can still find the control --
        // caught live by Lighthouse's label-content-name-mismatch audit.
        if surface.C then a'.attr("aria-label", "alias, alternate URL for this game") else a'
    // C: orientation for a non-visual arrival -- WHAT this is and HOW it's interacted with,
    // stated up front rather than left to be pieced together from 9 separate cell labels.
    // Visible (not screen-reader-only hidden text): this is real, shared content, not an
    // assistive-tech-only aside -- the dual-audience thesis this factor is supposed to test.
    // aria-describedby links it to the grid so it is ALSO announced at the point of
    // interaction (entering the grid), not only once at the top of the page.
    // NOT "game-intro-..." -- the test suite (and any other consumer) uses [id^=game-] to find
    // the board container; a second id sharing that prefix silently collides with it.
    let introId = $"intro-{gameId}"
    let gameIntro =
        p(id = introId, class' = "game-intro") {
            "Tic-tac-toe: a 3-by-3 grid game for two players, X and O. On your turn, select an "
            "empty square to claim it. Align three of your marks in a row, column, or diagonal to win."
        }
        :> HtmlElement
    let boardGrid = if surface.C then boardGrid.attr("aria-describedby", introId) else boardGrid
    div(id = $"game-{gameId}", class' = "game-board")
        .attr("data-game-status", statusToken)
        .attr("data-can-move", (if canMove then "true" else "false"))
        .attr("data-signals", sprintf "{gameId: '%s', player: '', position: ''}" gameId) {
        if surface.C then gameIntro else Fragment() { }
        // Canonical link + full id as text so the / -> /games/{id} trail is navigable without
        // JS and an agent can transcribe the id from the link text (a truncated label would
        // not be navigable).
        // Canonical link under the name this representation was served as, plus the alias link
        // (/games <-> /arenas are one resource under two names), so a client that entered by
        // either name can navigate the whole trail without ever crossing over.
        div(class' = "game-link") {
            a(href = sprintf "%s/%s" basePath gameId) { sprintf "Game %s" gameId }
            aliasLink
        }
        // C: role="status" announces turn/win/draw to assistive tech on both the JS-morph and
        // the no-JS refresh paths; aria-live polite keeps the JS-morph announcement.
        statusRegion
        boardGrid
        renderLegend assignment currentPlayer
        // Post-game gate (twin): a terminal game offers no controls, so an agent cannot
        // delete-then-create a replacement game and contaminate a run with a second game's moves.
        if isActive then renderControls surface basePath gameId else Fragment() { }
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

        /* role="row" wrapper (ARIA grid pattern) stays invisible to the CSS grid layout --
           its children lay out as if it were not there, same technique as .board form above. */
        .board-row { display: contents; }

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

        .game-intro {
            text-align: center;
            margin: 0 0 10px 0;
            font-size: 0.85em;
            color: #666;
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
            /* #2196F3 (Material Blue 500) failed WCAG AA at this 12px size, ~3.1:1 against
               white (axe/Lighthouse color-contrast, confirmed live). #1565C0 (Blue 800)
               passes at ~5.6:1; the existing hover shade (#1976D2, Blue 700, ~4.6:1) also
               already passed and is unchanged. */
            background-color: #1565C0;
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
            /* #f44336 (Material Red 500) also failed WCAG AA (~4.0:1). #C62828 (Red 800)
               passes at ~7.0:1; the existing hover shade (#d32f2f, Red 700, ~5.0:1) also
               already passed and is unchanged. */
            background-color: #C62828;
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
