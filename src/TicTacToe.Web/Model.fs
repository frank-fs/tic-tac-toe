module TicTacToe.Web.Model

// Player assignment types for multi-player support
// This module tracks which authenticated users are assigned to which game roles
// Also defines GamePhase DU for statechart integration (Web layer only, not Engine)

open System
open TicTacToe.Model

// ============================================================================
// GamePhase — statechart state (Web layer only, Engine untouched)
// ============================================================================

/// Game phase for statechart state machine. Maps from Engine's MoveResult.
/// RequireQualifiedAccess avoids shadowing MoveResult's XTurn/OTurn/Won/Draw/Error cases.
[<RequireQualifiedAccess>]
[<StructuralEquality; StructuralComparison>]
type GamePhase =
    | XTurn
    | OTurn
    | Won
    | Draw
    | Error

/// Events that trigger statechart transitions.
/// RequireQualifiedAccess to avoid shadowing Engine's MakeMove.
[<RequireQualifiedAccess>]
type GameEvent =
    | MakeMove of position: SquarePosition
    | Reset
    | Delete

/// Convert Engine's MoveResult to the statechart GamePhase.
let toGamePhase (result: MoveResult) : GamePhase =
    match result with
    | MoveResult.XTurn _ -> GamePhase.XTurn
    | MoveResult.OTurn _ -> GamePhase.OTurn
    | MoveResult.Won _ -> GamePhase.Won
    | MoveResult.Draw _ -> GamePhase.Draw
    | MoveResult.Error _ -> GamePhase.Error

/// Reasons why a move was rejected
type RejectionReason =
    | NotYourTurn // Correct player, wrong turn
    | NotAPlayer // User is spectator
    | WrongPlayer // User is X but O's turn (or vice versa)
    | GameOver // Game already finished

/// Result of validating whether a user can make a move
type MoveValidationResult =
    | Allowed
    | Rejected of RejectionReason

/// Tracks which authenticated user is assigned to which role (X or O) in a specific game
type PlayerAssignment =
    { GameId: string
      PlayerXId: string option
      PlayerOId: string option }

/// Creates a new empty player assignment for a game
let createAssignment gameId =
    { GameId = gameId
      PlayerXId = None
      PlayerOId = None }

/// Messages for the PlayerAssignmentManager MailboxProcessor
type PlayerAssignmentMessage =
    | TryAssignAndValidate of
        gameId: string *
        userId: string *
        isXTurn: bool *
        AsyncReplyChannel<MoveValidationResult * PlayerAssignment>
    | RemoveGame of gameId: string
    | GetAssignment of gameId: string * AsyncReplyChannel<PlayerAssignment option>

/// State for the PlayerAssignmentManager
type AssignmentState = Map<string, PlayerAssignment>

/// MailboxProcessor that manages player assignments for all games
type PlayerAssignmentManager() =
    let agent =
        MailboxProcessor<PlayerAssignmentMessage>.Start(fun inbox ->
            let rec loop (state: AssignmentState) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | TryAssignAndValidate(gameId, userId, isXTurn, reply) ->
                        let assignment =
                            state |> Map.tryFind gameId |> Option.defaultValue (createAssignment gameId)

                        let result, newAssignment =
                            match assignment.PlayerXId, assignment.PlayerOId, isXTurn with
                            // X slot open and it's X's turn - assign user as X
                            | None, _, true ->
                                let updated =
                                    { assignment with
                                        PlayerXId = Some userId }

                                Allowed, updated

                            // O slot open, it's O's turn, and user is not X - assign user as O
                            | Some xId, None, false when xId <> userId ->
                                let updated =
                                    { assignment with
                                        PlayerOId = Some userId }

                                Allowed, updated

                            // User is X and it's X's turn - allow
                            | Some xId, _, true when xId = userId -> Allowed, assignment

                            // User is O and it's O's turn - allow
                            | _, Some oId, false when oId = userId -> Allowed, assignment

                            // User is X but it's O's turn - not your turn
                            | Some xId, Some _, false when xId = userId -> Rejected NotYourTurn, assignment

                            // User is O but it's X's turn - not your turn
                            | Some _, Some oId, true when oId = userId -> Rejected NotYourTurn, assignment

                            // Both slots filled and user is neither - spectator trying to play
                            | Some xId, Some oId, _ when xId <> userId && oId <> userId ->
                                Rejected NotAPlayer, assignment

                            // Same user trying to claim O when they're X
                            | Some xId, None, false when xId = userId -> Rejected NotYourTurn, assignment

                            // Edge case: X slot open but O's turn (shouldn't happen in normal game flow)
                            | None, _, false -> Rejected NotAPlayer, assignment

                            // Catch-all for any unexpected state
                            | _ -> Rejected NotAPlayer, assignment

                        reply.Reply(result, newAssignment)
                        let newState = state |> Map.add gameId newAssignment
                        return! loop newState

                    | RemoveGame gameId ->
                        let newState = state |> Map.remove gameId
                        return! loop newState

                    | GetAssignment(gameId, reply) ->
                        reply.Reply(state |> Map.tryFind gameId)
                        return! loop state
                }

            loop Map.empty)

    /// Try to assign a player and validate the move
    member _.TryAssignAndValidate(gameId: string, userId: string, isXTurn: bool) =
        agent.PostAndReply(fun reply -> TryAssignAndValidate(gameId, userId, isXTurn, reply))

    /// Remove a game's player assignments (when game is deleted)
    member _.RemoveGame(gameId: string) = agent.Post(RemoveGame gameId)

    /// Get the current assignment for a game
    member _.GetAssignment(gameId: string) =
        agent.PostAndReply(fun reply -> GetAssignment(gameId, reply))
