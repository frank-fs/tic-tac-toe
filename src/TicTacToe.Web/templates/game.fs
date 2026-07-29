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

/// One <form> now wraps the whole board (see `renderGameBoard`) instead of one per square --
/// each submit button below carries its own `name="position" value="TopLeft"`, standard HTML
/// submit-button behavior: a no-JS POST includes it as a form field via the submitter, and
/// datastar's submit handler reads it off `evt.submitter.value` (a native SubmitEvent property,
/// not datastar-specific) to know which square fired. Was 9 forms + 18 hidden inputs; now 1
/// form + 1 hidden `player` field, since player is the same for every square in a render.

/// A submittable, empty square: the label names the LOCATION, its occupancy, and the claim
/// action in one phrase ("top left square, empty, claim it for X") -- a screen-reader user
/// hears what the square IS and what it does, not just a bare button name. Hides the decorative
/// X/O preview glyph from the a11y tree (its meaning is already in the label).
let private submitSquare (surface: Surface) (state: GameState) (playerStr: string) (position: SquarePosition) =
    let posStr = position.ToString()
    let btn =
        if surface.C then
            button(class' = "square square-clickable", type' = "submit", name = "position", value = posStr)
                .attr("aria-label", sprintf "%s square, empty, claim it for %s" (humanPosition position) playerStr) {
                span(class' = "preview").attr("aria-hidden", "true") { playerStr }
            }
        else
            button(class' = "square square-clickable", type' = "submit", name = "position", value = posStr) {
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
    let btn = button(class' = "square", type' = "submit", name = "position", value = position.ToString()) { label }
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
///   A=0: ALL 9 squares are submit buttons (naive design), every one genuinely submittable --
///        occupied/inactive ones aren't client-side disabled; the server rejects an illegal move
///        the same way for a browser click as it already does for an HTTP agent's raw POST.
///   A=1: ONLY the caller's currently-legal moves are submit buttons; every other square is a
///        plain cell. Submittable squares are named `position` buttons inside the one board-wide
///        form `renderGameBoard` wraps around all nine -- see the note above `submitSquare`.
let private renderSquare
    (surface: Surface) (legal: Set<SquarePosition>) (playerStr: string)
    (state: GameState) (isActive: bool) (position: SquarePosition) =
    let isTaken = match state.TryGetValue(position) with | true, Taken _ -> true | _ -> false
    if surface.A then
        if Set.contains position legal then
            submitSquare surface state playerStr position
        else
            renderPlainCell surface state (squareLabel state position) position
    else
        if isActive && not isTaken then submitSquare surface state playerStr position
        else disabledSquare surface state (squareLabel state position) position

/// Render the player legend showing X and O assignments.
/// LAYOUT NOTE: the dense grid has no room for two seat ids per tile, so `.legend` is now
/// visually hidden in gameStyles (position/clip, NOT display:none) -- the markup, the
/// legend-active class and the seat prose are all unchanged and still announced, so
/// 007-player-identity-legend's assertions on the rendered HTML still hold.
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

/// One control, now a submit BUTTON living inside the one board-wide form (see `renderGameBoard`)
/// rather than its own form -- `formaction` routes its native no-JS POST (the delete button's
/// formaction is the no-JS POST alias for the DELETE verb on the canonical resource); the shared
/// form's single submit-dispatch expression (`boardSubmitExpr`) branches on `evt.submitter.name`
/// to run the right datastar action when JS is present. `rel` keeps typing the affordance in the
/// markup, same vocabulary as before, just relocated from the (now-gone) wrapping form onto the
/// button. C: a11yLabel names WHICH game this control acts on -- in a multi-game dashboard a
/// screen reader's button list shows "Reset Game" x N indistinguishably without it.
///
/// LAYOUT NOTE: `label` is now a GLYPH ("↺"/"✕"), which is not an accessible name, so the
/// aria-label is emitted UNCONDITIONALLY here -- not gated on surface.C as it was when the
/// visible label was the prose "Reset Game". Without that, C=0 would ship two unnamed buttons
/// per board. The visible glyph carries no other information: `rel`, `name` and `formaction`
/// still type the affordance for an agent exactly as before.
let private controlButton (surface: Surface) (btnClass: string) (rel: string) (name: string) (formaction: string) (label: string) (a11yLabel: string) : HtmlElement =
    let btn = button(class' = btnClass, type' = "submit", name = name, formaction = formaction).attr("rel", rel) { label }
    btn.attr("aria-label", a11yLabel).attr("title", a11yLabel) :> HtmlElement

/// Reset/delete controls, verbatim from the twin: BOTH are always real, live submit buttons while
/// the game is in progress — no viewer/seat/count/lock gating in the markup. Authorization is the
/// HANDLER's job (403 not-a-player, 409 locked / would-drop-below-minimum). Gating them here would
/// change the affordance count an agent sees, which is the surface the banked results were
/// produced against.
let private renderControlButtons (surface: Surface) (basePath: string) gameId =
    let shortId = prefix8 gameId
    div(class' = "controls") {
        controlButton surface "reset-game-btn" "reset-game" "reset"
            (sprintf "%s/%s/reset" basePath gameId) "↺" (sprintf "Reset game %s" shortId)
        controlButton surface "delete-game-btn" "delete-game" "delete"
            (sprintf "%s/%s/delete" basePath gameId) "✕" (sprintf "Delete game %s" shortId)
    }

// ============================================================================
// Main Render Function
// ============================================================================

/// The one board-wide form's submit dispatch. Move squares fall through to the default (last)
/// branch -- they carry `name="position"`, never "reset"/"delete" -- and read their target square
/// off `evt.submitter.value`. Reset/delete buttons are told apart by `evt.submitter.name` (a
/// native SubmitEvent property, not datastar-specific) since one shared expression now covers all
/// three actions instead of each control's own form carrying its own.
let private boardSubmitExpr (basePath: string) gameId (playerStr: string) =
    let url = sprintf "%s/%s" basePath gameId
    sprintf
        "evt.submitter.name === 'reset' ? @post('%s/reset') : evt.submitter.name === 'delete' ? @delete('%s') : ($player = '%s', $position = evt.submitter.value, @post('%s'))"
        url url playerStr url

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
    let renderSquare = renderSquare surface legal playerStr state isActive
    // Stable, machine-readable status token so a no-JS agent can decide turn/outcome without
    // parsing the display prose; data-can-move says whether THIS viewer may move now.
    // LAYOUT NOTE: this token is now also the styling hook for "finished games recede" --
    // gameStyles selects on [data-game-status^="won-"] / ="draw". No new attribute needed.
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
        // LAYOUT NOTE: the dot is a purely decorative colour restatement of statusToken, so it is
        // aria-hidden and lives INSIDE the atomic region (one live region per board, as before).
        let d =
            div(class' = "status") {
                span(class' = "status-dot").attr("aria-hidden", "true")
                h2() { status }
            }
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
    // C: orientation for a non-visual arrival -- WHAT this is and HOW it's interacted with,
    // stated up front rather than left to be pieced together from 9 separate cell labels.
    // aria-describedby links it to the grid so it is ALSO announced at the point of
    // interaction (entering the grid), not only once at the top of the page.
    // NOT "game-intro-..." -- the test suite (and any other consumer) uses [id^=game-] to find
    // the board container; a second id sharing that prefix silently collides with it.
    //
    // LAYOUT NOTE: this used to be VISIBLE prose (the dual-audience thesis). It is now visually
    // hidden by `.game-intro` in gameStyles -- position/clip, so it stays in the accessibility
    // tree, stays a real aria-describedby target, and is still announced on entering the grid.
    // What changed is only that 15+ boards no longer repeat the same 40 words on screen; the
    // sighted-visitor equivalent now lives once per page (see home.fs's `.game-info`).
    let introId = $"intro-{gameId}"
    let gameIntro =
        p(id = introId, class' = "game-intro") {
            "Tic-tac-toe: a 3-by-3 grid game for two players, X and O. On your turn, select an "
            "empty square to claim it. Align three of your marks in a row, column, or diagonal to win."
        }
        :> HtmlElement
    let boardGrid = if surface.C then boardGrid.attr("aria-describedby", introId) else boardGrid
    // Canonical link + the id as text so the / -> /games/{id} trail is navigable without JS.
    // LAYOUT NOTE: the visible text is now the 8-char prefix instead of the full id, with the
    // full id kept in href (still transcribable, still navigable) and named in aria-label.
    // Moved from the board container into `.game-footer` below so it shares a row with the
    // controls; the `.game-link` class an existing consumer may select on is unchanged.
    let gameLink =
        let link = a(href = sprintf "%s/%s" basePath gameId) { prefix8 gameId }
        div(class' = "game-link") {
            if surface.C then link.attr("aria-label", sprintf "Open game %s" gameId) else link
        }
    // A=0 always wraps the board in a form (every square, including occupied/finished ones, is a
    // real submit target -- the naive-design thesis this factor tests). A=1 only wraps it when at
    // least one square is actually legal. Either way, once the game is in progress (isActive) the
    // reset/delete controls need the same wrapping form too -- there is exactly one form per game
    // board now, covering moves and controls alike; `boardSubmitExpr`'s dispatch (below) decides
    // which datastar action a given submit actually runs.
    let hasMoveForm = not surface.A || not (Set.isEmpty legal)
    let hasForm = hasMoveForm || isActive
    // LAYOUT NOTE: tile order is board -> status -> (hidden legend) -> footer, so the board is the
    // first thing in every cell of the grid and the rows read as a floor of boards. The footer's
    // min-height keeps a finished tile (no controls) exactly as tall as an active one, which is
    // what keeps the grid rows regular.
    let boardContent =
        Fragment() {
            if hasMoveForm then input(type' = "hidden", name = "player", value = playerStr)
            boardGrid
            statusRegion
            renderLegend assignment currentPlayer
            div(class' = "game-footer") {
                gameLink
                // Post-game gate (twin): a terminal game offers no controls, so an agent cannot
                // delete-then-create a replacement game and contaminate a run with a second game's moves.
                if isActive then renderControlButtons surface basePath gameId else Fragment() { }
            }
        }
    let boardSection =
        if hasForm then
            form(method = "post", action = sprintf "%s/%s" basePath gameId)
                .attr("rel", "make-move")
                .attr("data-on:submit__prevent", boardSubmitExpr basePath gameId playerStr) {
                boardContent
            }
            :> HtmlElement
        else
            boardContent :> HtmlElement
    div(id = $"game-{gameId}", class' = "game-board")
        .attr("data-game-status", statusToken)
        .attr("data-can-move", (if canMove then "true" else "false"))
        .attr("data-signals", sprintf "{gameId: '%s', player: '', position: ''}" gameId) {
        if surface.C then gameIntro else Fragment() { }
        boardSection
    }

/// CSS styles for the game board
let gameStyles =
    style() {
        raw
            """
        /* ====================================================================
           A park of boards: uniform tiles in a regular, auto-filling grid.
           Every tile is a fixed 110x110 box, so the column count follows from
           the container width with no per-tile measurement, and the pitch is a
           constant 134px (110 + 24 gap) -- which is what makes a windowed /
           virtualised renderer possible later without reflowing anything.
           ==================================================================== */

        .game-container {
            /* was max-width: 800px + centered: the floor now uses the viewport */
            max-width: none;
            margin: 0;
            padding: 0;
            font-family: Arial, sans-serif;
        }

        /* ---- toolbar ---- */
        .app-bar {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 24px;
            padding: 14px 24px;
            border-bottom: 1px solid #e6e4e1;
        }

        .app-bar-titles {
            display: flex;
            align-items: baseline;
            gap: 12px;
        }

        .title {
            /* was 2em, centered */
            margin: 0;
            font-size: 16px;
            font-weight: 600;
            color: #333;
            text-align: left;
        }

        .board-count {
            font-size: 12px;
            color: #666;
        }

        .new-game-container {
            margin: 0;
            text-align: initial;
        }

        /* The right-aligned identity chip stays in layout.fs; only its padding changes
           so it lines up with the toolbar and the grid below it. */
        .page-header {
            display: flex;
            justify-content: flex-end;
            padding: 8px 24px 0 24px;
        }

        .user-identity {
            font-family: monospace;
            font-size: 0.85em;
            color: #666;
            overflow: hidden;
            text-overflow: ellipsis;
            max-width: 120px;
        }

        /* ---- the floor ---- */
        .games-container {
            display: grid;
            grid-template-columns: repeat(auto-fill, 110px);
            gap: 28px 24px;
            justify-content: center;
            padding: 28px 24px;
        }

        /* ---- one table ---- */
        .game-board {
            width: 110px;
            box-sizing: border-box;
            padding: 7px;
            display: flex;
            flex-direction: column;
            gap: 7px;
            background-color: #fff;
            border-radius: 4px;
            /* was #f5f5f5 + 15px padding + box-shadow -- the card frame is what stopped
               this layout scaling past a dozen boards */
            box-shadow: none;
        }

        /* Finished games recede so games in play read first. Hook is the existing
           machine-readable status token, not a new attribute. */
        .game-board[data-game-status="draw"],
        .game-board[data-game-status^="won-"] {
            background-color: #fbfbfa;
        }

        /* ---- crosshatch board: no outer frame, uniform 2px interior lines ---- */
        .board {
            display: grid;
            grid-template-columns: repeat(3, 32px);
            width: 96px;
            margin: 0 auto;
            /* removed: background-color #333 + 4px padding + 4px grid-gap. That drew an
               outer border and doubled every interior line. */
        }

        /* role="row" wrapper (ARIA grid pattern) stays invisible to the CSS grid layout --
           its children lay out as if it were not there. */
        .board-row { display: contents; }

        .square {
            width: 32px;
            height: 32px;
            box-sizing: border-box;
            padding: 0;
            background-color: #fff;
            border-style: solid;
            border-color: #333;
            border-width: 0;              /* set per cell below */
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 15px;
            font-weight: bold;
            line-height: 1;
            cursor: default;
            transition: background-color 0.15s;
        }

        /* Collapsed borders: each interior line is drawn exactly once, and no cell on the
           outside edge draws one -- that is the whole crosshatch. .board-row is
           display:contents, so :nth-child still addresses the squares within a row. */
        .board-row .square:nth-child(-n+2) { border-right-width: 2px; }
        .board-row:nth-child(-n+2) .square { border-bottom-width: 2px; }

        .square-clickable {
            cursor: pointer;
            background-color: #f0f8ff;
        }

        .square-clickable:hover {
            background-color: #e6f3ff;
        }

        .square .player { color: #333; }
        .square .preview { color: #999; }
        .square .empty { color: #ccc; font-size: 13px; }

        /* ---- status: dot + text, inside one atomic live region ---- */
        .status {
            display: flex;
            align-items: center;
            gap: 6px;
            margin: 0;
            text-align: left;
        }

        .status h2 {
            margin: 0;
            font-size: 11px;
            font-weight: 400;
            line-height: 1.2;
            color: #555;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .game-board[data-game-status="draw"] .status h2,
        .game-board[data-game-status^="won-"] .status h2 {
            font-weight: 700;
            color: #666;
        }

        .status-dot {
            width: 5px;
            height: 5px;
            border-radius: 50%;
            flex: none;
            background-color: #4CAF50;
        }

        .game-board[data-game-status^="won-"] .status-dot { background-color: #1565C0; }
        .game-board[data-game-status="draw"] .status-dot { background-color: #a8a29a; }

        /* ---- footer: short id + controls, flush with the board's edges ----
           96px available == 48px (8 monospace chars) + 46px (2x22px buttons + 2px gap),
           so there is no flex `gap` here: space-between already separates the two groups
           and a gap would overflow the row by 2px. */
        .game-footer {
            display: flex;
            align-items: center;
            justify-content: space-between;
            min-height: 20px;
        }

        .game-link {
            margin: 0;
            font-size: 11px;
            text-align: left;
        }

        .game-link a {
            font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
            /* #666 is ~5.7:1 on white -- the value .user-identity already uses. The
               greys this design first reached for (#a8a29a, #8a8580) are 2.5:1 and
               3.6:1 and would fail AA at this size, same trap as the old #2196F3. */
            color: #666;
            text-decoration: none;
            white-space: nowrap;
            flex: none;
        }

        .game-link a:hover { color: #1565C0; text-decoration: underline; }

        /* ---- controls: 22px ghost icon buttons, accent on hover ---- */
        .controls {
            display: flex;
            gap: 2px;
            flex: none;
            text-align: initial;
        }

        .reset-game-btn,
        .delete-game-btn {
            width: 22px;
            height: 22px;
            padding: 0;
            margin: 0;
            font-size: 12px;
            line-height: 1;
            background-color: #fff;
            color: #666;
            border: 1px solid #c9c5bf;
            border-radius: 3px;
            cursor: pointer;
            transition: color 0.15s, border-color 0.15s;
        }

        /* #1565C0 / #C62828 are the AA-passing shades this file already settled on
           (#2196F3 was ~3.1:1 and #f44336 ~4.0:1 at small sizes). */
        .reset-game-btn:hover:not(:disabled) {
            color: #1565C0;
            border-color: #1565C0;
        }

        .delete-game-btn:hover:not(:disabled) {
            color: #C62828;
            border-color: #C62828;
        }

        .reset-game-btn:disabled,
        .delete-game-btn:disabled {
            color: #a8a29a;
            border-color: #e6e4e1;
            cursor: not-allowed;
        }

        /* ---- the open table, and the empty floor ----
           `order: 1` pins the open table to the END of the grid no matter where it sits in
           the DOM: a datastar morph that inserts a new board after it, or any future change
           to render order, cannot push it into the middle of the floor. Everything else in
           the grid keeps the default `order: 0`, so boards stay in creation order.
           The slot is withheld entirely at capacity (see home.fs) -- an affordance that
           cannot succeed should not be offered -- and `.at-capacity-slot` takes its place. */
        .add-game-slot {
            order: 1;
            width: 110px;
            height: 110px;
            background-color: #fff;
            border: 1px dashed #c9c5bf;
            border-radius: 4px;
            cursor: pointer;
            font-family: inherit;
            font-size: 22px;
            color: #666;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: border-color 0.2s, color 0.2s;
        }

        .add-game-slot:hover {
            border-color: #4CAF50;
            color: #4CAF50;
        }

        /* The form wrapper is a grid item too, so it carries the pin as well. */
        .add-game-form { order: 1; display: contents; }

        .at-capacity-slot {
            order: 1;
            width: 110px;
            height: 110px;
            box-sizing: border-box;
            padding: 10px;
            display: flex;
            align-items: center;
            justify-content: center;
            text-align: center;
            font-size: 11px;
            line-height: 1.3;
            color: #666;
            border: 1px dashed #ebe9e5;
            border-radius: 4px;
        }

        .empty-slot {
            width: 110px;
            height: 110px;
            border: 1px dashed #ebe9e5;
            border-radius: 4px;
            box-sizing: border-box;
        }

        /* ---- visually hidden, still in the accessibility tree ----
           Not display:none: these must stay announced and stay valid
           aria-describedby targets. */
        .game-intro,
        .legend {
            position: absolute;
            width: 1px;
            height: 1px;
            padding: 0;
            margin: 0;
            overflow: hidden;
            clip: rect(0 0 0 0);
            clip-path: inset(50%);
            white-space: nowrap;
            border: 0;
        }

        .legend-active { font-weight: bold; }

        /* ---- unchanged ---- */
        .new-game-btn {
            background-color: #4CAF50;
            color: white;
            padding: 8px 18px;
            font-size: 13px;
            border: none;
            border-radius: 4px;
            cursor: pointer;
            transition: background-color 0.2s;
        }

        .new-game-btn:hover {
            background-color: #45a049;
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

        .loading {
            text-align: center;
            color: #666;
            font-style: italic;
            padding: 40px;
        }

        .game-info {
            text-align: center;
            margin: 0;
            padding: 4px 24px 24px 24px;
            color: #666;
            font-size: 12px;
        }
        """
    }
