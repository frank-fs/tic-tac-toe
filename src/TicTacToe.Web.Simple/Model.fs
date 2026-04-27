module TicTacToe.Web.Simple.Model

open System

/// Reasons why a move was rejected
type RejectionReason =
    | NotYourTurn   // Correct player, wrong turn
    | NotAPlayer    // User is spectator
    | WrongPlayer   // User is X but O's turn (or vice versa)
    | GameOver      // Game already finished

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

/// MailboxProcessor that manages player assignments for all arenas
/// No broadcast infrastructure — request/response only
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
                                let updated = { assignment with PlayerXId = Some userId }
                                Allowed, updated

                            // O slot open, it's O's turn, and user is not X - assign user as O
                            | Some xId, None, false when xId <> userId ->
                                let updated = { assignment with PlayerOId = Some userId }
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

                            // X slot open but O's turn
                            | None, _, false -> Rejected NotAPlayer, assignment

                            // Catch-all
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

    member _.TryAssignAndValidate(gameId: string, userId: string, isXTurn: bool) =
        agent.PostAndReply(fun reply -> TryAssignAndValidate(gameId, userId, isXTurn, reply))

    member _.RemoveGame(gameId: string) = agent.Post(RemoveGame gameId)

    member _.GetAssignment(gameId: string) =
        agent.PostAndReply(fun reply -> GetAssignment(gameId, reply))
