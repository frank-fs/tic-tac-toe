module TicTacToe.Web.Mcp

// The MCP (RPC null-hypothesis) surface, folded directly into TicTacToe.Web so it shares the SAME
// GameSupervisor, PlayerAssignmentManager, and EventLog singletons as the HTTP surface — one engine,
// one live game, two interfaces. No separate MCP-side seat/turn authority: this module adapts to the
// existing HTTP-side PlayerAssignmentManager (Model.fs) rather than reimplementing it.

open System.Collections.Concurrent
open System.ComponentModel
open System.Text.Json
open System.Text.Json.Nodes
open ModelContextProtocol.Server
open TicTacToe.Model
open TicTacToe.Engine
open TicTacToe.Web.Model
open TicTacToe.Web.EventLog

type AuthResponse = { token: string }
type NewGameResponse = { gameId: string }

/// Final snapshot of a finished game, retained for reads after the supervisor drops it.
type CompletedState =
    { Board: JsonObject; WhoseTurn: string; Status: string }

/// Cross-request state that MUST outlive a single tool invocation (the MCP SDK instantiates the tool
/// class per tools/call). Also the LOGIN REGISTRY: each MCP client authenticates once and re-presents
/// its own token per request (its cookie jar) — identity is NOT a seat, X/O are assigned later by move
/// order via the shared PlayerAssignmentManager.
type ToolState() =
    let issued = ConcurrentDictionary<string, bool>()
    member val CompletedGames = ConcurrentDictionary<string, CompletedState>()

    member _.Mint() : string =
        let t = System.Guid.NewGuid().ToString("N")
        issued.[t] <- true
        t

    member _.IsIssued(token: string) : bool =
        not (System.String.IsNullOrWhiteSpace token) && issued.ContainsKey token

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

/// Board as 9 named squares (TopLeft..BottomRight) -> "X" | "O" | "". Named keys (not a positional
/// array) so the agent reads squares by name. MCP-specific presentation (HTTP renders HTML instead);
/// this is not duplicated engine/assignment logic, just this protocol's own response shape.
let renderBoard (gs: GameState) : JsonObject =
    let obj = JsonObject()
    for pos in allPositions do
        let label =
            match gs.TryGetValue pos with
            | true, Taken X -> "X"
            | true, Taken O -> "O"
            | _ -> ""
        obj[pos.ToString()] <- JsonValue.Create(label)
    obj

let private stateOf (result: MoveResult) : GameState =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

// Turn wording kept consistent with the HTTP arm's board status so both protocols message
// whose-turn identically to the agent.
let whoseTurnStr (result: MoveResult) =
    match result with
    | XTurn _ -> "X's turn"
    | OTurn _ -> "O's turn"
    | Won(_, p) -> sprintf "%O won" p
    | Draw _ -> "draw"
    | Error _ -> "error"

let statusStr (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error(_, msg) -> sprintf "error: %s" msg

let validMoves (result: MoveResult) : string[] =
    match result with
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

/// Outcome of a move attempt, ready to be boxed into the MCP JSON response.
type MoveOutcome =
    | Moved of board: JsonObject * whoseTurn: string * status: string * side: Player * newlySeated: bool
    | Rejected of code: string

/// Minimal Result computation expression for linear short-circuit composition.
type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return(x) = Ok x
    member _.ReturnFrom(r: Result<_, _>) = r

let private result = ResultBuilder()

let private requireToken (token: string option) : Result<string, string> =
    match token with
    | Some t -> Ok t
    | None -> Result.Error "unauthenticated"

let private requireGame (supervisor: GameSupervisor) (gameId: string) : Result<Game, string> =
    match supervisor.GetGame gameId with
    | Some g -> Ok g
    | None -> Result.Error "game_not_found"

let private requirePlayable (game: Game) : Result<MoveResult, string> =
    match game.GetState() with
    | Won _
    | Draw _ -> Result.Error "game_over"
    | TicTacToe.Model.MoveResult.Error _ -> Result.Error "invalid_move"
    | (XTurn _ | OTurn _) as before -> Ok before

/// Validate the caller's turn via the SHARED PlayerAssignmentManager — the same authority and the same
/// before/after diffing pattern Handlers.fs uses for the HTTP arm's player_assigned log (Handlers.fs
/// ~L497-521): capture the assignment before the call, diff against after to detect a newly-bound seat,
/// since PlayerAssignmentManager itself carries no such flag (by design — it's the HTTP arm's own API,
/// unmodified for this reuse).
let private requireSeat
    (assignments: PlayerAssignmentManager)
    (gameId: string)
    (token: string)
    (before: MoveResult)
    : Result<Player * bool, string> =
    let isXTurn = (match before with | XTurn _ -> true | _ -> false)
    let priorAssignment = assignments.GetAssignment(gameId)

    match assignments.TryAssignAndValidate(gameId, token, isXTurn) with
    | MoveValidationResult.Rejected NotYourTurn, _ -> Result.Error "not_your_turn"
    | MoveValidationResult.Rejected WrongPlayer, _ -> Result.Error "not_your_turn"
    | MoveValidationResult.Rejected NotAPlayer, _ -> Result.Error "game_full"
    | MoveValidationResult.Rejected GameOver, _ -> Result.Error "game_over"
    | MoveValidationResult.Allowed, newAssignment ->
        let side = if isXTurn then X else O
        Ok(side, Some newAssignment <> priorAssignment)

let private requireEmptySquare (before: MoveResult) (position: string) : Result<SquarePosition, string> =
    match SquarePosition.TryParse position with
    | None -> Result.Error "invalid_input"
    | Some pos ->
        match (stateOf before).TryGetValue pos with
        | true, Taken _ -> Result.Error "position_taken"
        | _ -> Ok pos

/// Resolve a move attempt: authenticate, locate game, validate turn via the shared assignment
/// manager, derive the side from the claim, apply, and shape the result.
let resolveMove
    (supervisor: GameSupervisor)
    (assignments: PlayerAssignmentManager)
    (token: string option)
    (gameId: string)
    (position: string)
    : MoveOutcome =
    let outcome =
        result {
            let! token = requireToken token
            let! game = requireGame supervisor gameId
            let! before = requirePlayable game
            // Validate the square BEFORE seating: requireSeat mutates the assignment manager (binds the
            // seat and signals newlySeated), so it must run only on a move that will actually be applied —
            // else a first move onto a taken square would bind the seat and lose the player_assigned log
            // for the real move.
            let! pos = requireEmptySquare before position
            let! side, newlySeated = requireSeat assignments gameId token before
            let move = match side with | X -> XMove pos | O -> OMove pos
            game.MakeMove move
            let after = game.GetState()
            return (renderBoard (stateOf after), whoseTurnStr after, statusStr after, side, newlySeated)
        }

    match outcome with
    | Ok(board, whoseTurn, status, side, newlySeated) -> Moved(board, whoseTurn, status, side, newlySeated)
    | Result.Error code -> MoveOutcome.Rejected code

/// Read-only: which seat (if any) this token already holds, for attributing rejected moves to a role.
let private seatOf (assignments: PlayerAssignmentManager) (gameId: string) (token: string) : string option =
    match assignments.GetAssignment(gameId) with
    | Some a when a.PlayerXId = Some token -> Some "X"
    | Some a when a.PlayerOId = Some token -> Some "O"
    | _ -> None

[<McpServerToolType>]
type TicTacToeTools
    (
        supervisor: GameSupervisor,
        assignments: PlayerAssignmentManager,
        eventLog: EventLog,
        toolState: ToolState
    ) =

    let completedGames = toolState.CompletedGames

    [<McpServerTool>]
    [<Description("Authenticate to obtain your identity token. Pass it as identityToken on every make_move so the server recognizes you. This is your login identity, not a seat — X or O is decided later by move order.")>]
    member _.authenticate() : AuthResponse =
        { token = toolState.Mint() }

    [<McpServerTool>]
    [<Description("List all active in-progress games. Returns an array of {gameId, whoseTurn, status}.")>]
    member _.list_games() : string =
        let games =
            supervisor.ListActiveGames()
            |> List.choose (fun id ->
                match supervisor.GetGame id with
                | None -> None
                | Some game ->
                    let result = game.GetState()
                    let obj = JsonObject()
                    obj["gameId"] <- JsonValue.Create(id)
                    obj["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
                    obj["status"] <- JsonValue.Create(statusStr result)
                    Some(obj :> JsonNode))
        JsonSerializer.Serialize(games)

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns a gameId to use in subsequent calls. X always moves first. Only one game may be active at a time; if one already exists this returns {error: \"MaxGamesReached\"}.")>]
    member _.new_game() : obj =
        match supervisor.TryCreateGame(Some 1) with
        | None -> box {| error = "MaxGamesReached" |}
        | Some(gameId, _) -> box { gameId = gameId }

    [<McpServerTool>]
    [<Description("Make a move. Authenticate first to get an identity token, then pass it as identityToken; the server derives your side (X or O) from it by move order. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.make_move
        (
            [<Description("The game ID returned by new_game")>] gameId: string,
            [<Description("Board position: TopLeft | TopCenter | TopRight | MiddleLeft | MiddleCenter | MiddleRight | BottomLeft | BottomCenter | BottomRight")>] position: string,
            [<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue("")>]
            [<Description("Your identity token from authenticate(). The server derives your side (X or O) from it by move order.")>] identityToken: string
        ) : obj =
        let token = if toolState.IsIssued identityToken then Some identityToken else None

        match resolveMove supervisor assignments token gameId position with
        | Moved(board, turn, status, side, newlySeated) ->
            let role = side.ToString()
            if newlySeated then eventLog.LogEvent("player_assigned", gameId, role = role)
            eventLog.LogEvent("move_accepted", gameId, role = role, move = position)
            if status = "won" || status = "draw" then
                completedGames[gameId] <- { Board = board; WhoseTurn = turn; Status = status }
                let moveCount =
                    board |> Seq.filter (fun kv -> kv.Value <> null && kv.Value.GetValue<string>() <> "") |> Seq.length
                let outcome =
                    if status = "draw" then "draw"
                    else $"""{turn.Split(' ').[0].ToLower()}_wins"""
                eventLog.LogEvent("game_over", gameId, outcome = outcome, moveCount = moveCount)
            box {| board = board; whoseTurn = turn; status = status |}
        | MoveOutcome.Rejected code ->
            let role = token |> Option.bind (seatOf assignments gameId) |> Option.defaultValue "unassigned"
            eventLog.LogEvent("move_rejected", gameId, role = role, reason = code)
            box {| error = code; position = position |}

    [<McpServerTool>]
    [<Description("Get the current board state for a game: 9 squares keyed by name (TopLeft..BottomRight) with value \"X\"/\"O\"/\"\", whose turn it is, status, and valid moves.")>]
    member _.get_board([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame gameId with
        | Some game ->
            let result = game.GetState()
            eventLog.LogEvent("state_read", gameId, whoseTurn = whoseTurnStr result)
            box
                {| board = renderBoard (stateOf result)
                   whoseTurn = whoseTurnStr result
                   status = statusStr result
                   validMoves = validMoves result |}
        | None ->
            match completedGames.TryGetValue gameId with
            | true, c ->
                box
                    {| board = c.Board
                       whoseTurn = c.WhoseTurn
                       status = c.Status
                       validMoves = ([||]: string[]) |}
            | _ -> box {| error = "game_not_found"; gameId = gameId |}

    [<McpServerTool>]
    [<Description("Get full game state including gameId, board (9 squares keyed by name, value \"X\"/\"O\"/\"\"), whose turn, status, and valid moves.")>]
    member _.get_state([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame gameId with
        | Some game ->
            let result = game.GetState()
            eventLog.LogEvent("state_read", gameId, whoseTurn = whoseTurnStr result)
            box
                {| gameId = gameId
                   board = renderBoard (stateOf result)
                   whoseTurn = whoseTurnStr result
                   status = statusStr result
                   validMoves = validMoves result |}
        | None ->
            match completedGames.TryGetValue gameId with
            | true, c ->
                box
                    {| gameId = gameId
                       board = c.Board
                       whoseTurn = c.WhoseTurn
                       status = c.Status
                       validMoves = ([||]: string[]) |}
            | _ -> box {| error = "game_not_found"; gameId = gameId |}
