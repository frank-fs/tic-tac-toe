module TicTacToe.Orchestrator.Metrics

open TicTacToe.Orchestrator.Types

let private rolesFromEvents (events: ServerLogEvent list) : string list =
    events
    |> List.choose (function
        | PlayerAssigned(_, role, _) -> Some role
        | MoveAccepted(_, role, _, _) -> Some role
        | MoveRejected(_, role, _, _) -> Some role
        | _ -> None)
    |> List.distinct

// Each arm names the same rejection differently (Simple "OutOfTurn", Proto "NotYourTurn",
// ERPC "not_your_turn"). Normalize before comparing so out-of-turn is cross-arm comparable.
let private isOutOfTurn (reason: string) : bool =
    match reason.ToLowerInvariant().Replace("_", "") with
    | "outofturn" | "notyourturn" -> true
    | _ -> false

let resolveRoles (events: ServerLogEvent list) : RoleAssignment list =
    rolesFromEvents events
    |> List.map (fun role -> { AgentId = ""; Role = role })

let deriveOutcome (events: ServerLogEvent list) (allAbandoned: bool) : string * string =
    let gameOverEvent =
        events |> List.tryPick (function GameOver(_, outcome, _, _) -> Some outcome | _ -> None)
    match gameOverEvent with
    | Some outcome -> (outcome, "server_log")
    | None -> ("abandoned", "abandoned")

let computePerRoleMetrics
    (transcripts: AgentTranscript list)
    (events: ServerLogEvent list)
    : Map<string, PerAgentMetrics> =

    rolesFromEvents events |> List.map (fun role ->
        let accepted =
            events |> List.filter (function MoveAccepted(_, r, _, _) -> r = role | _ -> false) |> List.length
        let rejected =
            events |> List.filter (function MoveRejected(_, r, _, _) -> r = role | _ -> false) |> List.length
        let outOfTurn =
            events |> List.filter (function
                | MoveRejected(_, r, reason, _) -> r = role && isOutOfTurn reason
                | _ -> false) |> List.length
        let total = accepted + rejected

        let rpva =
            if role = "Observer" || accepted = 0 then None
            else Some(float total / float accepted)
        let invalidRate =
            if total = 0 then 0.0
            else float rejected / float total

        role, {
            Rpva = rpva
            InvalidRate = invalidRate
            OutOfTurnAttempts = outOfTurn
            Tokens = 0
        }) |> Map.ofList

let computeCellMetrics
    (cellId: string)
    (transcripts: AgentTranscript list)
    (roles: RoleAssignment list)
    (events: ServerLogEvent list)
    (durationSeconds: float)
    (startedAt: System.DateTimeOffset)
    (totalTokens: int)
    : CellMetrics =

    let allAbandoned = transcripts |> List.forall (fun t -> t.Aborted)
    let (outcome, signal) = deriveOutcome events allAbandoned
    let perAgent = computePerRoleMetrics transcripts events

    { CellId = cellId
      Outcome = outcome
      CompletionSignal = signal
      DurationSeconds = durationSeconds
      RoleAssignments = roles
      PerAgent = perAgent
      TotalTokens = totalTokens }
