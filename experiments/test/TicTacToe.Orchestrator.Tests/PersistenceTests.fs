module TicTacToe.Orchestrator.Tests.PersistenceTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Persistence
open TicTacToe.Orchestrator.Personas

let private dummyMetrics cellId = {
    CellId = cellId
    Outcome = "x_wins"
    CompletionSignal = "server_log"
    DurationSeconds = 42.3
    RoleAssignments = [
        { AgentId = "agent-1"; Role = "X" }
        { AgentId = "agent-2"; Role = "O" }
        { AgentId = "agent-3"; Role = "Observer" }
    ]
    PerAgent = Map.ofList [
        "X", { Rpva = Some 1.2; InvalidRate = 0.0; OutOfTurnAttempts = 0; Tokens = 1000 }
        "O", { Rpva = Some 1.5; InvalidRate = 0.05; OutOfTurnAttempts = 1; Tokens = 900 }
        "Observer", { Rpva = None; InvalidRate = 1.0; OutOfTurnAttempts = 4; Tokens = 500 }
    ]
}

let private dummyTranscript agentId = {
    AgentId = agentId
    PersonaName = "beginner"
    LlmTurns = []
    Aborted = false
}

let private dummyCell cellId = {
    Id = cellId
    Variant = Simple
    Personas = (beginner, beginner, beginner)
    Model = "test-model"
    InitialGames = 1
    MaxGames = 1
    MaxTurnsPerAgent = 50
    McpServers = []
    Temperature = 0.0
}

[<TestFixture>]
type SaveCellTests() =

    [<Test>]
    member _.``saveCell creates correct directory structure``() =
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let cellId = "smoke-simple-bbb"
        let result = {
            CellSpec = dummyCell cellId
            Transcripts = Map.ofList [
                "agent-1", dummyTranscript "agent-1"
                "agent-2", dummyTranscript "agent-2"
                "agent-3", dummyTranscript "agent-3"
            ]
            ServerLogs = [ GameCreated("g1", DateTimeOffset.UtcNow) ]
            Metrics = dummyMetrics cellId
        }

        saveCell root cellId result

        let cellDir = Path.Combine(root, "experiments", "results", "smoke-simple-bbb")
        Assert.That(Directory.Exists(cellDir), Is.True)
        Assert.That(File.Exists(Path.Combine(cellDir, "transcripts", "agent-1.json")), Is.True)
        Assert.That(File.Exists(Path.Combine(cellDir, "transcripts", "agent-2.json")), Is.True)
        Assert.That(File.Exists(Path.Combine(cellDir, "transcripts", "agent-3.json")), Is.True)
        Assert.That(File.Exists(Path.Combine(cellDir, "metrics.json")), Is.True)
        Assert.That(File.Exists(Path.Combine(cellDir, "cell-spec.json")), Is.True)

        Directory.Delete(root, true)

    [<Test>]
    member _.``metrics.json contains valid JSON with expected outcome``() =
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let cellId = "smoke-test-cell"
        let result = {
            CellSpec = dummyCell cellId
            Transcripts = Map.empty
            ServerLogs = []
            Metrics = dummyMetrics cellId
        }

        saveCell root cellId result

        let metricsPath = Path.Combine(root, "experiments", "results", cellId, "metrics.json")
        let json = File.ReadAllText(metricsPath)
        let doc = JsonDocument.Parse(json)
        Assert.That(doc.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("x_wins"))
        Assert.That(doc.RootElement.GetProperty("cell_id").GetString(), Is.EqualTo(cellId))

        Directory.Delete(root, true)
