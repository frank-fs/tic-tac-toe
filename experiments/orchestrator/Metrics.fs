module TicTacToe.Orchestrator.Metrics

open TicTacToe.Orchestrator.Types

let resolveRoles (events: ServerLogEvent list) (sessionMap: Map<string, string>) : RoleAssignment list =
    let assignedSessions =
        events
        |> List.choose (function
            | PlayerAssigned(_, sid, role, _) -> Some(sid, role)
            | _ -> None)
        |> Map.ofList

    sessionMap |> Map.toList |> List.map (fun (agentId, sid) ->
        let role =
            assignedSessions
            |> Map.tryFind sid
            |> Option.defaultValue "Observer"
        { AgentId = agentId; Role = role })

let deriveOutcome (events: ServerLogEvent list) (allAbandoned: bool) : string * string =
    let gameOverEvent =
        events |> List.tryPick (function GameOver(_, outcome, _, _) -> Some outcome | _ -> None)
    match gameOverEvent with
    | Some outcome -> (outcome, "server_log")
    | None -> ("abandoned", "abandoned")

let computePerAgentMetrics
    (transcripts: AgentTranscript list)
    (roles: RoleAssignment list)
    (sessionMap: Map<string, string>)
    (events: ServerLogEvent list)
    : Map<string, PerAgentMetrics> =

    let tokens (t: AgentTranscript) =
        t.LlmTurns |> List.sumBy (fun turn -> turn.InputTokens + turn.OutputTokens)

    roles |> List.map (fun ra ->
        let sid = sessionMap |> Map.tryFind ra.AgentId |> Option.defaultValue ""
        let agentTranscript = transcripts |> List.tryFind (fun t -> t.AgentId = ra.AgentId)
        let agentTokens = agentTranscript |> Option.map tokens |> Option.defaultValue 0

        let accepted =
            events |> List.filter (function MoveAccepted(_, s, _, _) -> s = sid | _ -> false) |> List.length
        let rejected =
            events |> List.filter (function MoveRejected(_, s, _, _) -> s = sid | _ -> false) |> List.length
        let outOfTurn =
            events |> List.filter (function
                | MoveRejected(_, s, reason, _) -> s = sid && reason = "OutOfTurn"
                | _ -> false) |> List.length
        let total = accepted + rejected

        let rpva =
            if ra.Role = "Observer" || accepted = 0 then None
            else Some(float total / float accepted)
        let invalidRate =
            if total = 0 then 0.0
            else float rejected / float total

        ra.Role, {
            Rpva = rpva
            InvalidRate = invalidRate
            OutOfTurnAttempts = outOfTurn
            Tokens = agentTokens
        }) |> Map.ofList

let computeCellMetrics
    (cellId: string)
    (transcripts: AgentTranscript list)
    (roles: RoleAssignment list)
    (sessionMap: Map<string, string>)
    (events: ServerLogEvent list)
    (durationSeconds: float)
    (startedAt: System.DateTimeOffset)
    : CellMetrics =

    let allAbandoned = transcripts |> List.forall (fun t -> t.Aborted)
    let (outcome, signal) = deriveOutcome events allAbandoned
    let perAgent = computePerAgentMetrics transcripts roles sessionMap events

    { CellId = cellId
      Outcome = outcome
      CompletionSignal = signal
      DurationSeconds = durationSeconds
      RoleAssignments = roles
      PerAgent = perAgent }
