module TicTacToe.Orchestrator.Tests.MetricsTests

open System
open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics

let private ts = DateTimeOffset.UtcNow

let private makeTranscript agentId turns aborted = {
    AgentId = agentId
    PersonaName = "beginner"
    LlmTurns = turns
    Aborted = aborted
}

let private makeTurn toolCalls = {
    TurnIndex = 0
    StopReason = if List.isEmpty toolCalls then "end_turn" else "tool_use"
    InputTokens = 100
    OutputTokens = 20
    ToolCalls = toolCalls
    TextOutput = None
    Timestamp = ts
}

let private makeToolCall name output = {
    ToolName = name
    Input = "{}"
    Output = Some output
    Error = None
    DurationMs = 10
    Timestamp = ts
}

[<TestFixture>]
type RoleAttributionTests() =

    [<Test>]
    member _.``resolveRoles maps session_id to role from PlayerAssigned events``() =
        let events = [
            PlayerAssigned("g1", "sess-a", "X", ts)
            PlayerAssigned("g1", "sess-b", "O", ts)
        ]
        let sessionMap = Map.ofList [("agent-1", "sess-a"); ("agent-2", "sess-b"); ("agent-3", "sess-c")]
        let roles = resolveRoles events sessionMap
        let role1 = roles |> List.find (fun r -> r.AgentId = "agent-1") |> fun r -> r.Role
        let role2 = roles |> List.find (fun r -> r.AgentId = "agent-2") |> fun r -> r.Role
        let role3 = roles |> List.find (fun r -> r.AgentId = "agent-3") |> fun r -> r.Role
        Assert.That(role1, Is.EqualTo("X"))
        Assert.That(role2, Is.EqualTo("O"))
        Assert.That(role3, Is.EqualTo("Observer"))

[<TestFixture>]
type PerAgentMetricsTests() =

    [<Test>]
    member _.``X player: 3 moves accepted, 1 rejected → rpva 4/3, invalid_rate 0.25``() =
        let serverEvents = [
            MoveAccepted("g1", "sess-x", "TopLeft", ts)
            MoveRejected("g1", "sess-o", "OutOfTurn", ts)
            MoveAccepted("g1", "sess-x", "MiddleCenter", ts)
            MoveRejected("g1", "sess-x", "PositionTaken", ts)
            MoveAccepted("g1", "sess-x", "TopRight", ts)
        ]
        let roles = [
            { AgentId = "agent-1"; Role = "X" }
            { AgentId = "agent-2"; Role = "O" }
        ]
        let sessionMap = Map.ofList [("agent-1", "sess-x"); ("agent-2", "sess-o")]
        let t1 = makeTranscript "agent-1" [makeTurn [makeToolCall "make_move" "{}"]] false
        let t2 = makeTranscript "agent-2" [makeTurn [makeToolCall "make_move" "{}"]] false
        let metrics = computePerAgentMetrics [t1; t2] roles sessionMap serverEvents
        let xMetrics = metrics |> Map.find "X"
        Assert.That(xMetrics.Rpva.Value, Is.EqualTo(4.0 / 3.0).Within(0.001))
        Assert.That(xMetrics.InvalidRate, Is.EqualTo(0.25).Within(0.001))
        Assert.That(xMetrics.OutOfTurnAttempts, Is.EqualTo(0))

    [<Test>]
    member _.``observer has null rpva and high invalid_rate``() =
        let serverEvents = [
            MoveRejected("g1", "sess-obs", "NotAPlayer", ts)
            MoveRejected("g1", "sess-obs", "NotAPlayer", ts)
        ]
        let roles = [
            { AgentId = "agent-1"; Role = "X" }
            { AgentId = "agent-2"; Role = "O" }
            { AgentId = "agent-3"; Role = "Observer" }
        ]
        let sessionMap = Map.ofList [("agent-1", "sess-x"); ("agent-2", "sess-o"); ("agent-3", "sess-obs")]
        let t3 = makeTranscript "agent-3" [makeTurn [makeToolCall "make_move" "{}"]] false
        let metrics = computePerAgentMetrics [t3] roles sessionMap serverEvents
        let obsMetrics = metrics |> Map.find "Observer"
        Assert.That(obsMetrics.Rpva, Is.EqualTo(None))
        Assert.That(obsMetrics.InvalidRate, Is.EqualTo(1.0).Within(0.001))

[<TestFixture>]
type OutcomeTests() =

    [<Test>]
    member _.``GameOver x_wins event → outcome x_wins, signal server_log``() =
        let events = [ GameOver("g1", "x_wins", 5, ts) ]
        let (outcome, signal) = deriveOutcome events false
        Assert.That(outcome, Is.EqualTo("x_wins"))
        Assert.That(signal, Is.EqualTo("server_log"))

    [<Test>]
    member _.``no GameOver event + aborted → outcome abandoned``() =
        let (outcome, signal) = deriveOutcome [] true
        Assert.That(outcome, Is.EqualTo("abandoned"))
        Assert.That(signal, Is.EqualTo("abandoned"))
