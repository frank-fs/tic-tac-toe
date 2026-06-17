module TicTacToe.McpRpc.Identity

open System
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
        AsyncReplyChannel<MoveValidationResult>

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
                        reply.Reply result
                        return! loop (state |> Map.add gameId updated)
                }

            loop Map.empty)

    /// Assign the token to an open seat (lazily, on first move) and validate the turn.
    member _.TryAssignAndValidate(gameId: string, token: string, isXTurn: bool) : MoveValidationResult =
        agent.PostAndReply(fun reply -> TryAssign(gameId, token, isXTurn, reply))

/// Per-connection authenticated identity. On stdio one process == one
/// connection == one agent, so a single instance is the connection's session.
type SessionIdentity() =
    let mutable token: string option = None

    /// Mint a new identity token for this connection and return it.
    member _.Authenticate() : string =
        let t = Guid.NewGuid().ToString("N")
        token <- Some t
        t

    /// The currently authenticated token, if any.
    member _.Current: string option = token

open TicTacToe.Engine

/// Outcome of a move attempt, ready to be boxed into the MCP JSON response.
type MoveOutcome =
    | Moved of board: string[] * whoseTurn: string * status: string
    | Rejected of code: string

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) : string[] =
    allPositions
    |> Array.map (fun pos ->
        match gs.TryGetValue pos with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private stateOf (result: MoveResult) : GameState =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

let private whoseTurnStr (result: MoveResult) =
    match result with
    | XTurn _ -> "X"
    | OTurn _ -> "O"
    | Won(_, p) -> sprintf "%O won" p
    | Draw _ -> "draw"
    | Error _ -> "error"

let private statusStr (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error(_, msg) -> sprintf "error: %s" msg

/// Resolve a move attempt: authenticate, locate game, validate turn via the
/// assignment store, derive the side from the claim, apply, and shape the result.
let resolveMove
    (supervisor: GameSupervisor)
    (assignments: PlayerAssignmentStore)
    (token: string option)
    (gameId: string)
    (position: string)
    : MoveOutcome =
    match token with
    | None -> Rejected "unauthenticated"
    | Some token ->
        match supervisor.GetGame gameId with
        | None -> Rejected "game_not_found"
        | Some game ->
            match game.GetState() with
            | Won _
            | Draw _ -> Rejected "game_over"
            | Error _ -> Rejected "invalid_move"
            | (XTurn _ | OTurn _) as before ->
                let isXTurn = (match before with | XTurn _ -> true | _ -> false)

                match assignments.TryAssignAndValidate(gameId, token, isXTurn) with
                | MoveValidationResult.Rejected NotYourTurn -> Rejected "not_your_turn"
                | MoveValidationResult.Rejected NotAPlayer -> Rejected "game_full"
                | MoveValidationResult.Allowed side ->
                    match SquarePosition.TryParse position with
                    | None -> Rejected "invalid_input"
                    | Some pos ->
                        match (stateOf before).TryGetValue pos with
                        | true, Taken _ -> Rejected "position_taken"
                        | _ ->
                            let move = match side with | X -> XMove pos | O -> OMove pos
                            game.MakeMove move
                            let after = game.GetState()
                            Moved(renderBoard (stateOf after), whoseTurnStr after, statusStr after)
