module TicTacToe.McpRpc.Identity

open TicTacToe.Model

/// Why a move was rejected (subset relevant to the ERPC arm).
type RejectionReason =
    | NotYourTurn
    | NotAPlayer

/// Result of assigning + validating a caller's move.
type MoveValidationResult =
    | Allowed of Player
    | Rejected of RejectionReason

/// Per-game seat binding: token assigned to X and/or O.
type private Assignment =
    { PlayerXId: string option
      PlayerOId: string option }

let private emptyAssignment = { PlayerXId = None; PlayerOId = None }

type private StoreMessage =
    | TryAssign of
        gameId: string *
        token: string *
        isXTurn: bool *
        AsyncReplyChannel<MoveValidationResult * bool>
    | SeatOf of
        gameId: string *
        token: string *
        AsyncReplyChannel<string option>

/// Thread-safe (token, gameId) -> seat binding. Decision table mirrors
/// src/TicTacToe.Web/Model.fs:58-103, emitting the assigned side on success.
type PlayerAssignmentStore() =
    let decide (a: Assignment) (token: string) (isXTurn: bool) : MoveValidationResult * Assignment =
        match a.PlayerXId, a.PlayerOId, isXTurn with
        | None, _, true -> Allowed X, { a with PlayerXId = Some token }
        | Some xId, None, false when xId <> token -> Allowed O, { a with PlayerOId = Some token }
        | Some xId, _, true when xId = token -> Allowed X, a
        | _, Some oId, false when oId = token -> Allowed O, a
        | Some xId, Some _, false when xId = token -> Rejected NotYourTurn, a
        | Some _, Some oId, true when oId = token -> Rejected NotYourTurn, a
        | Some xId, Some oId, _ when xId <> token && oId <> token -> Rejected NotAPlayer, a
        | Some xId, None, false when xId = token -> Rejected NotYourTurn, a
        | None, _, false -> Rejected NotAPlayer, a
        | _ -> Rejected NotAPlayer, a

    let agent =
        MailboxProcessor<StoreMessage>.Start(fun inbox ->
            let rec loop (state: Map<string, Assignment>) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | TryAssign(gameId, token, isXTurn, reply) ->
                        let current = state |> Map.tryFind gameId |> Option.defaultValue emptyAssignment
                        let result, updated = decide current token isXTurn
                        // newlySeated: this move bound a seat for the first time (None -> Some). The mailbox
                        // is serialized, so this is the single race-free source of "a player was assigned".
                        reply.Reply(result, updated <> current)
                        return! loop (state |> Map.add gameId updated)
                    | SeatOf(gameId, token, reply) ->
                        let current = state |> Map.tryFind gameId |> Option.defaultValue emptyAssignment
                        let seat =
                            if current.PlayerXId = Some token then Some "X"
                            elif current.PlayerOId = Some token then Some "O"
                            else None
                        reply.Reply seat
                        return! loop state
                }

            loop Map.empty)

    /// Assign the token to an open seat (lazily, on first move) and validate the turn. Returns the
    /// validation result and whether THIS call bound a new seat (for a race-free player_assigned log).
    member _.TryAssignAndValidate(gameId: string, token: string, isXTurn: bool) : MoveValidationResult * bool =
        agent.PostAndReply(fun reply -> TryAssign(gameId, token, isXTurn, reply))

    /// Read-only: which seat (if any) this token already holds in the game. Used to
    /// attribute rejected moves to the caller's role for per-role metrics.
    member _.SeatOf(gameId: string, token: string) : string option =
        agent.PostAndReply(fun reply -> SeatOf(gameId, token, reply))

open TicTacToe.Engine
open System.Text.Json.Nodes

/// Outcome of a move attempt, ready to be boxed into the MCP JSON response.
type MoveOutcome =
    | Moved of board: JsonObject * whoseTurn: string * status: string * side: Player * newlySeated: bool
    | Rejected of code: string

let allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

/// Board as 9 named squares (TopLeft..BottomRight) -> "X" | "O" | "".
/// Named keys (not a positional array) so the agent reads squares by name.
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

let stateOf (result: MoveResult) : GameState =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

// Turn wording kept consistent with the HTTP arm's board status (templates/game.fs statusText:
// "X's turn" / "O's turn") so both protocols message whose-turn identically to the agent. (Won/Draw
// strings are unchanged; the game_over outcome parse in Tools.fs splits only the Won string.)
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

/// Only an in-progress game (X or O to move) is playable; otherwise reject.
let private requirePlayable (game: Game) : Result<MoveResult, string> =
    match game.GetState() with
    | Won _
    | Draw _ -> Result.Error "game_over"
    | TicTacToe.Model.MoveResult.Error _ -> Result.Error "invalid_move"
    | (XTurn _ | OTurn _) as before -> Ok before

let private requireSeat
    (assignments: PlayerAssignmentStore)
    (gameId: string)
    (token: string)
    (before: MoveResult)
    : Result<Player * bool, string> =
    let isXTurn = (match before with | XTurn _ -> true | _ -> false)

    match assignments.TryAssignAndValidate(gameId, token, isXTurn) with
    | MoveValidationResult.Rejected NotYourTurn, _ -> Result.Error "not_your_turn"
    | MoveValidationResult.Rejected NotAPlayer, _ -> Result.Error "game_full"
    | MoveValidationResult.Allowed side, newlySeated -> Ok(side, newlySeated)

/// Parse the target square and confirm it is empty in the pre-move board.
let private requireEmptySquare (before: MoveResult) (position: string) : Result<SquarePosition, string> =
    match SquarePosition.TryParse position with
    | None -> Result.Error "invalid_input"
    | Some pos ->
        match (stateOf before).TryGetValue pos with
        | true, Taken _ -> Result.Error "position_taken"
        | _ -> Ok pos

/// Resolve a move attempt: authenticate, locate game, validate turn via the
/// assignment store, derive the side from the claim, apply, and shape the result.
let resolveMove
    (supervisor: GameSupervisor)
    (assignments: PlayerAssignmentStore)
    (token: string option)
    (gameId: string)
    (position: string)
    : MoveOutcome =
    let outcome =
        result {
            let! token = requireToken token
            let! game = requireGame supervisor gameId
            let! before = requirePlayable game
            // Validate the square BEFORE seating: requireSeat mutates the assignment store (binds the seat
            // and signals newlySeated), so it must run only on a move that will actually be applied — else a
            // first move onto a taken square would bind the seat, consume newlySeated, and lose the
            // player_assigned log for the real move.
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
