# Orchestrator Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single-agent orchestrator with a three-agent (X, O, Observer) MailboxProcessor architecture where roles emerge from server enforcement, tool access is via MCP, and three instrumentation layers (LLM history, tool calls, server JSONL logs) are captured per cell.

**Architecture:** `Orchestrator.fs` iterates the cell matrix sequentially, starting a server, three `Agent` MailboxProcessors, and a `ServerLogTail` per cell. Agents connect to MCP servers independently (own processes, own cookie jars). The orchestrator signals stop after `game_over` appears in the server log, then persists transcripts and metrics. F# async throughout; no PowerShell, no shell scripts.

**Tech Stack:** F# .NET 10, existing `ModelContextProtocol` prerelease (also used for `TicTacToe.Mcp`), `LlmClient.fs` unchanged for Anthropic API calls. NUnit tests for Metrics, ServerLogTail, Persistence.

> **Prerequisite:** Plans `2026-06-11-tictactoe-mcp.md` and `2026-06-11-server-structured-logging.md` must be complete before running the smoke matrix.

---

## File Structure

| Action | Path | Responsibility |
|--------|------|---------------|
| **Rewrite** | `experiments/orchestrator/Types.fs` | All domain types: CellSpec, AgentConfig, transcripts, metrics, server log events |
| **Create** | `experiments/orchestrator/Personas.fs` | F# data: beginner, expert, chaos persona records |
| **Create** | `experiments/orchestrator/ServerLogTail.fs` | JSONL tail, event parsing, channel-based feed |
| **Create** | `experiments/orchestrator/McpClient.fs` | Per-agent MCP connection set, tool list aggregation, call dispatch |
| **Rewrite** | `experiments/orchestrator/Metrics.fs` | Per-agent RPVA, role attribution, outcome derivation |
| **Create** | `experiments/orchestrator/Persistence.fs` | Write per-cell directory: transcripts, server-logs, metrics.json |
| **Create** | `experiments/orchestrator/Agent.fs` | Per-agent MailboxProcessor: LLM turn loop, MCP tool execution |
| **Create** | `experiments/orchestrator/Orchestrator.fs` | Top-level MailboxProcessor: matrix iteration, cell lifecycle |
| **Create** | `experiments/orchestrator/Matrices/Smoke.fs` | Smoke matrix CellSpec list |
| **Rewrite** | `experiments/orchestrator/Program.fs` | Matrix selector, launches Orchestrator |
| **Modify** | `experiments/orchestrator/TicTacToe.Orchestrator.fsproj` | Compile list update, add ModelContextProtocol |
| **Delete** | `experiments/orchestrator/HttpAgent.fs` | Superseded by Agent.fs |
| **Delete** | `experiments/orchestrator/RpcAgent.fs` | Superseded by Agent.fs + TicTacToe.Mcp |
| **Delete** | `experiments/orchestrator/Classifier.fs` | Superseded by server log Layer 3 |
| **Delete** | `experiments/orchestrator/Runner.fs` | Superseded by Orchestrator.fs |
| **Rewrite** | `test/TicTacToe.Orchestrator.Tests/MetricsTests.fs` | Per-agent RPVA tests |
| **Delete** | `test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs` | Classifier deleted |
| **Create** | `test/TicTacToe.Orchestrator.Tests/ServerLogTailTests.fs` | JSONL parse tests |
| **Create** | `test/TicTacToe.Orchestrator.Tests/PersistenceTests.fs` | File layout tests |
| **Modify** | `test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj` | Compile list |

---

### Task 1: Rewrite Types.fs

**Files:**
- Modify: `experiments/orchestrator/Types.fs`

- [ ] **Step 1: Replace `experiments/orchestrator/Types.fs` with the new domain model**

```fsharp
module TicTacToe.Orchestrator.Types

open System
open System.Text.Json.Nodes
open System.Text.Json.Serialization

// ── Cell matrix ───────────────────────────────────────────────────────────────

type Variant = Proto | Simple | ERPC

type Persona = {
    Name: string
    SystemPrompt: string
}

type McpServerConfig = {
    Name: string
    Command: string
    Arguments: string[]
}

type CellSpec = {
    Id: string
    Variant: Variant
    Personas: Persona * Persona * Persona  // X-launch, O-launch, Observer-launch; roles emergent
    Model: string                          // LM Studio model id, passed verbatim
    InitialGames: int                      // TICTACTOE_INITIAL_GAMES env var
    MaxGames: int                          // TICTACTOE_MAX_GAMES env var
    MaxTurnsPerAgent: int
    McpServers: McpServerConfig list       // same tool set for all three agents
    Temperature: float
}

type AgentConfig = {
    Id: string          // "agent-1" | "agent-2" | "agent-3"
    Persona: Persona
    Model: string
    BaseUrl: string
    McpServers: McpServerConfig list
    MaxTurns: int
    Temperature: float
}

// ── Instrumentation: Layer 2 (tool calls) ────────────────────────────────────

type ToolCallRecord = {
    [<JsonPropertyName("tool_name")>] ToolName: string
    [<JsonPropertyName("input")>] Input: string           // JSON string
    [<JsonPropertyName("output")>] Output: string option  // JSON string, None on error
    [<JsonPropertyName("error")>] Error: string option
    [<JsonPropertyName("duration_ms")>] DurationMs: int
    [<JsonPropertyName("timestamp")>] Timestamp: DateTimeOffset
}

// ── Instrumentation: Layer 1 (LLM turns) ─────────────────────────────────────

type LlmTurn = {
    [<JsonPropertyName("turn_index")>] TurnIndex: int
    [<JsonPropertyName("stop_reason")>] StopReason: string  // "tool_use" | "end_turn" | "max_tokens"
    [<JsonPropertyName("input_tokens")>] InputTokens: int
    [<JsonPropertyName("output_tokens")>] OutputTokens: int
    [<JsonPropertyName("tool_calls")>] ToolCalls: ToolCallRecord list
    [<JsonPropertyName("text_output")>] TextOutput: string option
    [<JsonPropertyName("timestamp")>] Timestamp: DateTimeOffset
}

type AgentTranscript = {
    [<JsonPropertyName("agent_id")>] AgentId: string
    [<JsonPropertyName("persona")>] PersonaName: string
    [<JsonPropertyName("llm_turns")>] LlmTurns: LlmTurn list
    [<JsonPropertyName("aborted")>] Aborted: bool
}

type AgentSnapshot = {
    AgentId: string
    TurnIndex: int
    Aborted: bool
}

// ── Instrumentation: Layer 3 (server log events) ─────────────────────────────

type ServerLogEvent =
    | GameCreated     of gameId: string * timestamp: DateTimeOffset
    | PlayerAssigned  of gameId: string * sessionId: string * role: string * timestamp: DateTimeOffset
    | MoveAccepted    of gameId: string * sessionId: string * move: string * timestamp: DateTimeOffset
    | GameOver        of gameId: string * outcome: string * moveCount: int * timestamp: DateTimeOffset
    | MoveRejected    of gameId: string * sessionId: string * reason: string * timestamp: DateTimeOffset

// ── Metrics ───────────────────────────────────────────────────────────────────

type PerAgentMetrics = {
    [<JsonPropertyName("rpva")>] Rpva: float option          // None for observer
    [<JsonPropertyName("invalid_rate")>] InvalidRate: float
    [<JsonPropertyName("out_of_turn_attempts")>] OutOfTurnAttempts: int
    [<JsonPropertyName("tokens")>] Tokens: int
}

type RoleAssignment = {
    [<JsonPropertyName("agent_id")>] AgentId: string
    [<JsonPropertyName("role")>] Role: string  // "X" | "O" | "Observer"
}

type CellMetrics = {
    [<JsonPropertyName("cell_id")>] CellId: string
    [<JsonPropertyName("outcome")>] Outcome: string  // "x_wins" | "o_wins" | "draw" | "abandoned"
    [<JsonPropertyName("completion_signal")>] CompletionSignal: string  // "server_log" | "abandoned"
    [<JsonPropertyName("duration_seconds")>] DurationSeconds: float
    [<JsonPropertyName("role_assignments")>] RoleAssignments: RoleAssignment list
    [<JsonPropertyName("per_agent")>] PerAgent: Map<string, PerAgentMetrics>  // keyed by role
}

type CellResult = {
    CellSpec: CellSpec
    Transcripts: Map<string, AgentTranscript>  // keyed by agent_id
    ServerLogs: ServerLogEvent list
    Metrics: CellMetrics
}

// ── MailboxProcessor messages ─────────────────────────────────────────────────

type AgentMsg =
    | StartAgent
    | StopAgent  of AsyncReplyChannel<AgentTranscript>
    | GetSnapshot of AsyncReplyChannel<AgentSnapshot>

type OrchestratorMsg =
    | RunMatrix of cells: CellSpec list * AsyncReplyChannel<unit>
    | Shutdown  of AsyncReplyChannel<unit>

// ── Helpers ───────────────────────────────────────────────────────────────────

module Variant =
    let toString = function Proto -> "proto" | Simple -> "simple" | ERPC -> "erpc"
    let defaultPort = function Proto -> 5228 | Simple -> 5328 | ERPC -> 0
    let projectPath = function
        | Proto  -> "src/TicTacToe.Web/TicTacToe.Web.fsproj"
        | Simple -> "src/TicTacToe.Web.Simple/TicTacToe.Web.Simple.fsproj"
        | ERPC   -> ""  // no server for ERPC — TicTacToe.Mcp launched per agent via MCP
```

- [ ] **Step 2: Build the orchestrator project (expect compile errors from deleted modules — fix in Task 11)**

```bash
dotnet build experiments/orchestrator/ 2>&1 | grep -E "error|warning" | head -20
```

Expected: compile errors in HttpAgent.fs, RpcAgent.fs, Classifier.fs, Runner.fs, Program.fs — all will be fixed in later tasks.

---

### Task 2: Create Personas.fs

**Files:**
- Create: `experiments/orchestrator/Personas.fs`

- [ ] **Step 1: Write `experiments/orchestrator/Personas.fs`**

```fsharp
module TicTacToe.Orchestrator.Personas

open TicTacToe.Orchestrator.Types

let beginner : Persona = {
    Name = "beginner"
    SystemPrompt =
        "You are playing a web-based game. Use the http_request tool to interact with it by following the affordances (links, forms, and actions) in each server response. Start by visiting the URL you've been given. Read responses carefully and follow the links and form actions you find. Make moves when prompted."
}

let expert : Persona = {
    Name = "expert"
    SystemPrompt =
        "You are an expert HTTP agent playing a web-based game. Use the http_request tool efficiently. Inspect response headers and body for affordances. Track cookie-based session state across requests. Prefer following server-provided links over constructing URLs manually. Minimise redundant GET requests."
}

let chaos : Persona = {
    Name = "chaos"
    SystemPrompt =
        "You are a chaos agent probing a web-based game for weaknesses. Use the http_request tool to attempt actions that may be invalid: play out-of-turn, attempt moves on squares already taken, try to claim both player roles, send malformed inputs, repeat rejected requests. Record what works and what doesn't."
}

let get = function
    | "beginner" -> beginner
    | "expert"   -> expert
    | "chaos"    -> chaos
    | name       -> failwithf "Unknown persona: %s" name
```

- [ ] **Step 2: Add `Personas.fs` to compile list in `TicTacToe.Orchestrator.fsproj`**

Add `<Compile Include="Personas.fs" />` immediately after `Types.fs` in the `<ItemGroup>`.

---

### Task 3: Rewrite Metrics.fs and tests

**Files:**
- Rewrite: `experiments/orchestrator/Metrics.fs`
- Rewrite: `test/TicTacToe.Orchestrator.Tests/MetricsTests.fs`

- [ ] **Step 1: Write failing tests in `MetricsTests.fs`**

Replace the entire file:

```fsharp
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
        // Agent 1 has session sess-a, agent 2 has sess-b, agent 3 has sess-c
        let sessionMap = Map.ofList [("agent-1", "sess-a"); ("agent-2", "sess-b"); ("agent-3", "sess-c")]
        let roles = resolveRoles events sessionMap
        Assert.That(roles |> List.find (fun r -> r.AgentId = "agent-1") |> fun r -> r.Role, Is.EqualTo("X"))
        Assert.That(roles |> List.find (fun r -> r.AgentId = "agent-2") |> fun r -> r.Role, Is.EqualTo("O"))
        Assert.That(roles |> List.find (fun r -> r.AgentId = "agent-3") |> fun r -> r.Role, Is.EqualTo("Observer"))

[<TestFixture>]
type PerAgentMetricsTests() =

    [<Test>]
    member _.``X player: 3 moves accepted, 1 rejected → rpva 4.0, invalid_rate 0.25``() =
        // 3 MoveAccepted + 1 MoveRejected for agent-1 (X)
        let serverEvents = [
            MoveAccepted("g1", "sess-x", "TopLeft", ts)
            MoveRejected("g1", "sess-o", "OutOfTurn", ts)   // O rejected — different agent
            MoveAccepted("g1", "sess-x", "MiddleCenter", ts)
            MoveRejected("g1", "sess-x", "PositionTaken", ts)  // X rejected
            MoveAccepted("g1", "sess-x", "TopRight", ts)
        ]
        let roles = [
            { AgentId = "agent-1"; Role = "X" }
            { AgentId = "agent-2"; Role = "O" }
        ]
        let sessionMap = Map.ofList [("agent-1", "sess-x"); ("agent-2", "sess-o")]
        let t1 = makeTranscript "agent-1" [makeTurn [makeToolCall "make_move" "{}"]] false
        let t2 = makeTranscript "agent-2" [makeTurn [makeToolCall "make_move" "{}"]] false
        let transcripts = [t1; t2]

        let metrics = computePerAgentMetrics transcripts roles sessionMap serverEvents
        let xMetrics = metrics |> Map.find "X"

        // X: 3 accepted, 1 rejected (PositionTaken) → total actions = 4, valid = 3
        // RPVA = total / valid = 4 / 3 ≈ 1.33
        Assert.That(xMetrics.Rpva, Is.Not.EqualTo(None))
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
        let events = [
            GameOver("g1", "x_wins", 5, ts)
        ]
        let (outcome, signal) = deriveOutcome events false
        Assert.That(outcome, Is.EqualTo("x_wins"))
        Assert.That(signal, Is.EqualTo("server_log"))

    [<Test>]
    member _.``no GameOver event + aborted → outcome abandoned``() =
        let (outcome, signal) = deriveOutcome [] true
        Assert.That(outcome, Is.EqualTo("abandoned"))
        Assert.That(signal, Is.EqualTo("abandoned"))
```

- [ ] **Step 2: Run tests — expect compile failures**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: compile errors because `Metrics.fs` does not yet export `resolveRoles`, `computePerAgentMetrics`, `deriveOutcome`.

- [ ] **Step 3: Write `experiments/orchestrator/Metrics.fs`**

```fsharp
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
    | None when allAbandoned -> ("abandoned", "abandoned")
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
```

- [ ] **Step 4: Run tests — expect all pass**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: all metrics tests green.

- [ ] **Step 5: Commit**

```bash
git add experiments/orchestrator/Types.fs experiments/orchestrator/Personas.fs \
        experiments/orchestrator/Metrics.fs \
        test/TicTacToe.Orchestrator.Tests/MetricsTests.fs
git commit -m "feat(orchestrator): new Types, Personas, Metrics for three-agent design"
```

---

### Task 4: Create ServerLogTail.fs and tests

**Files:**
- Create: `experiments/orchestrator/ServerLogTail.fs`
- Create: `test/TicTacToe.Orchestrator.Tests/ServerLogTailTests.fs`

- [ ] **Step 1: Write failing tests in `test/TicTacToe.Orchestrator.Tests/ServerLogTailTests.fs`**

```fsharp
module TicTacToe.Orchestrator.Tests.ServerLogTailTests

open System
open System.IO
open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.ServerLogTail

[<TestFixture>]
type ParseEventTests() =

    [<Test>]
    member _.``game_created event parsed correctly``() =
        let json = """{"event_type":"game_created","game_id":"abc","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (GameCreated("abc", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected GameCreated, got: {ev}")

    [<Test>]
    member _.``player_assigned event parsed correctly``() =
        let json = """{"event_type":"player_assigned","game_id":"g1","session_id":"s1","role":"X","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (PlayerAssigned("g1", "s1", "X", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected PlayerAssigned, got: {ev}")

    [<Test>]
    member _.``move_accepted event parsed correctly``() =
        let json = """{"event_type":"move_accepted","game_id":"g1","session_id":"s1","move":"TopLeft","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (MoveAccepted("g1", "s1", "TopLeft", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected MoveAccepted, got: {ev}")

    [<Test>]
    member _.``game_over event parsed correctly``() =
        let json = """{"event_type":"game_over","game_id":"g1","outcome":"x_wins","move_count":5,"timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (GameOver("g1", "x_wins", 5, _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected GameOver, got: {ev}")

    [<Test>]
    member _.``request log entry with rejection_reason parsed as MoveRejected``() =
        let json = """{"request_id":"r1","session_id":"s1","game_id":"g1","player_role":"X","method":"POST","path":"/arenas/g1","status_code":403,"rejection_reason":"OutOfTurn","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (MoveRejected("g1", "s1", "OutOfTurn", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected MoveRejected, got: {ev}")

    [<Test>]
    member _.``request log without rejection_reason returns None``() =
        let json = """{"request_id":"r1","session_id":"s1","game_id":"g1","method":"GET","path":"/arenas/g1","status_code":200,"timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        Assert.That(ev, Is.EqualTo(None))

    [<Test>]
    member _.``malformed JSON returns None``() =
        let ev = parseLogLine "{not valid json"
        Assert.That(ev, Is.EqualTo(None))

[<TestFixture>]
type TailTests() =

    [<Test>]
    member _.``getEvents returns all parsed events from a file``() =
        let path = Path.GetTempFileName()
        File.WriteAllLines(path, [|
            """{"event_type":"game_created","game_id":"g1","timestamp":"2026-06-11T10:00:00Z"}"""
            """{"event_type":"player_assigned","game_id":"g1","session_id":"s1","role":"X","timestamp":"2026-06-11T10:00:01Z"}"""
        |])
        let tail = startTail path
        let events = tail.GetEvents()
        Assert.That(events.Length, Is.EqualTo(2))
        File.Delete(path)
```

- [ ] **Step 2: Add `ServerLogTailTests.fs` to test project compile list**

In `test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj`, add:
```xml
<Compile Include="ServerLogTailTests.fs" />
```

- [ ] **Step 3: Run tests — expect compile failures**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: compile error — `ServerLogTail` module not found.

- [ ] **Step 4: Write `experiments/orchestrator/ServerLogTail.fs`**

```fsharp
module TicTacToe.Orchestrator.ServerLogTail

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open TicTacToe.Orchestrator.Types

let parseLogLine (json: string) : ServerLogEvent option =
    try
        let obj = JsonNode.Parse(json) :?> JsonObject
        let ts () = DateTimeOffset.Parse(obj["timestamp"].GetValue<string>())

        let mutable evTypeNode: JsonNode = null
        if obj.TryGetPropertyValue("event_type", &evTypeNode) && evTypeNode <> null then
            match evTypeNode.GetValue<string>() with
            | "game_created" ->
                Some(GameCreated(obj["game_id"].GetValue<string>(), ts()))
            | "player_assigned" ->
                Some(PlayerAssigned(
                    obj["game_id"].GetValue<string>(),
                    obj["session_id"].GetValue<string>(),
                    obj["role"].GetValue<string>(),
                    ts()))
            | "move_accepted" ->
                Some(MoveAccepted(
                    obj["game_id"].GetValue<string>(),
                    obj["session_id"].GetValue<string>(),
                    obj["move"].GetValue<string>(),
                    ts()))
            | "game_over" ->
                Some(GameOver(
                    obj["game_id"].GetValue<string>(),
                    obj["outcome"].GetValue<string>(),
                    obj["move_count"].GetValue<int>(),
                    ts()))
            | _ -> None
        else
            // Request log entry — only keep 403 rejections
            let mutable statusNode: JsonNode = null
            let mutable rejNode: JsonNode = null
            let mutable gidNode: JsonNode = null
            let mutable sidNode: JsonNode = null
            if obj.TryGetPropertyValue("status_code", &statusNode) && statusNode <> null
               && statusNode.GetValue<int>() = 403
               && obj.TryGetPropertyValue("rejection_reason", &rejNode) && rejNode <> null
               && obj.TryGetPropertyValue("game_id", &gidNode) && gidNode <> null
               && obj.TryGetPropertyValue("session_id", &sidNode) && sidNode <> null then
                try
                    Some(MoveRejected(
                        gidNode.GetValue<string>(),
                        sidNode.GetValue<string>(),
                        rejNode.GetValue<string>(),
                        ts()))
                with _ -> None
            else None
    with _ -> None

/// Reads all JSONL events from a file. Non-blocking snapshot.
type LogTail(path: string) =
    member _.GetEvents() =
        if not (File.Exists(path)) then []
        else
            File.ReadAllLines(path)
            |> Array.toList
            |> List.choose parseLogLine

let startTail (path: string) : LogTail = LogTail(path)
```

- [ ] **Step 5: Add `ServerLogTail.fs` to orchestrator compile list**

In `TicTacToe.Orchestrator.fsproj`, add after `Personas.fs`:
```xml
<Compile Include="ServerLogTail.fs" />
```

- [ ] **Step 6: Run tests — expect all pass**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: all ServerLogTail tests and Metrics tests green.

- [ ] **Step 7: Commit**

```bash
git add experiments/orchestrator/ServerLogTail.fs \
        test/TicTacToe.Orchestrator.Tests/ServerLogTailTests.fs \
        test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj
git commit -m "feat(orchestrator): ServerLogTail JSONL parser + tests"
```

---

### Task 5: Create Persistence.fs and tests

**Files:**
- Create: `experiments/orchestrator/Persistence.fs`
- Create: `test/TicTacToe.Orchestrator.Tests/PersistenceTests.fs`

- [ ] **Step 1: Write `test/TicTacToe.Orchestrator.Tests/PersistenceTests.fs`**

```fsharp
module TicTacToe.Orchestrator.Tests.PersistenceTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Persistence

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
    Personas = (Personas.beginner, Personas.beginner, Personas.beginner)
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
```

- [ ] **Step 2: Add `PersistenceTests.fs` to test compile list**

- [ ] **Step 3: Run tests — expect compile failures**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: `Persistence` module not found.

- [ ] **Step 4: Write `experiments/orchestrator/Persistence.fs`**

```fsharp
module TicTacToe.Orchestrator.Persistence

open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open TicTacToe.Orchestrator.Types

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.Converters.Add(JsonStringEnumConverter())
    opts

let private writeJson (path: string) (value: 'a) =
    let json = JsonSerializer.Serialize(value, jsonOptions)
    File.WriteAllText(path, json)

let saveCell (repoRoot: string) (cellId: string) (result: CellResult) =
    let cellDir = Path.Combine(repoRoot, "experiments", "results", cellId)
    let transcriptsDir = Path.Combine(cellDir, "transcripts")
    Directory.CreateDirectory(transcriptsDir) |> ignore

    for kvp in result.Transcripts do
        let path = Path.Combine(transcriptsDir, $"{kvp.Key}.json")
        writeJson path kvp.Value

    writeJson (Path.Combine(cellDir, "metrics.json")) result.Metrics
    writeJson (Path.Combine(cellDir, "cell-spec.json")) result.CellSpec

let saveManifest (repoRoot: string) (matrixName: string) (cells: CellSpec list) (results: CellResult list) =
    let manifestDir = Path.Combine(repoRoot, "experiments", "results", matrixName)
    Directory.CreateDirectory(manifestDir) |> ignore
    let manifest = {|
        matrix = matrixName
        cells = results |> List.map (fun r -> {|
            cell_id = r.CellSpec.Id
            outcome = r.Metrics.Outcome
            completion_signal = r.Metrics.CompletionSignal
        |})
    |}
    writeJson (Path.Combine(manifestDir, "manifest.json")) manifest
```

- [ ] **Step 5: Add `Persistence.fs` to compile list in orchestrator fsproj**

- [ ] **Step 6: Run tests — expect all pass**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: all tests green.

- [ ] **Step 7: Commit**

```bash
git add experiments/orchestrator/Persistence.fs \
        test/TicTacToe.Orchestrator.Tests/PersistenceTests.fs
git commit -m "feat(orchestrator): Persistence saves per-cell JSON + manifest"
```

---

### Task 6: Update ServerProcess.fs

**Files:**
- Modify: `experiments/orchestrator/ServerProcess.fs`

- [ ] **Step 1: Read existing `ServerProcess.fs` to understand the current API**

The current `ServerProcess.startServer` accepts `(repoRoot, commit, variant)`. Update to also accept env vars from `CellSpec`.

- [ ] **Step 2: Update `ServerProcess.fs`**

Find the `startServer` function signature and add an overload that accepts a `CellSpec`:

```fsharp
// New overload accepting CellSpec
let startServerForCell (repoRoot: string) (cell: CellSpec) : Task<ServerHandle> =
    let logPath = Path.Combine(repoRoot, "experiments", "results", cell.Id, "server-requests.jsonl")
    // Ensure output dir exists
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)) |> ignore
    let extraEnv = [|
        "TICTACTOE_INITIAL_GAMES", string cell.InitialGames
        "TICTACTOE_MAX_GAMES", string cell.MaxGames
        "TICTACTOE_REQUEST_LOG_PATH", logPath
    |]
    startServerWithEnv repoRoot "HEAD" cell.Variant extraEnv
```

Also rename the internal helper to `startServerWithEnv` and update the original `startServer` to call it with empty extra env.

The full updated internal helper should thread extra env vars into the `ProcessStartInfo.Environment` dictionary before starting the process. Add the env var pairs directly to the environment if they're not empty.

---

### Task 7: Create McpClient.fs

**Files:**
- Create: `experiments/orchestrator/McpClient.fs`

> **Note:** The exact ModelContextProtocol .NET SDK API may differ slightly from what is shown below. Verify against the installed SDK version with `dotnet add package ModelContextProtocol --prerelease` and inspect the assembly's public types.

- [ ] **Step 1: Add ModelContextProtocol client package to orchestrator**

```bash
dotnet add experiments/orchestrator/TicTacToe.Orchestrator.fsproj package ModelContextProtocol --prerelease
```

- [ ] **Step 2: Write `experiments/orchestrator/McpClient.fs`**

```fsharp
module TicTacToe.Orchestrator.McpClient

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open ModelContextProtocol.Client
open TicTacToe.Orchestrator.LlmClient  // for ToolDef type
open TicTacToe.Orchestrator.Types

/// One connected MCP server client.
type McpConnection(client: IMcpClient, tools: ToolDef list) =
    member _.Tools = tools
    member _.CallToolAsync(name: string, args: Map<string, JsonNode>) : Task<string> =
        task {
            let argsDict = Dictionary<string, obj>()
            for kvp in args do
                argsDict.[kvp.Key] <- kvp.Value :> obj
            let! result = client.CallToolAsync(name, argsDict)
            let text =
                result.Content
                |> Seq.tryFind (fun c -> c.Type = "text")
                |> Option.map (fun c -> c.Text)
                |> Option.defaultValue ""
            return text
        }
    interface IDisposable with
        member _.Dispose() = (client :> IDisposable).Dispose()

/// Manages all MCP server connections for a single agent.
type McpClientSet(configs: McpServerConfig list) =
    let mutable connections: McpConnection list = []

    member _.InitializeAsync() : Task =
        task {
            for cfg in configs do
                let opts = StdioClientTransportOptions(
                    Name = cfg.Name,
                    Command = cfg.Command,
                    Arguments = cfg.Arguments)
                let transport = StdioClientTransport(opts)
                let! client = McpClientFactory.CreateAsync(transport)
                let! toolList = client.ListToolsAsync()
                let tools =
                    toolList.Tools
                    |> Seq.map (fun t ->
                        { Name = t.Name
                          Description = t.Description
                          InputSchema = JsonNode.Parse(t.InputSchema |> string) })
                    |> Seq.toList
                connections <- McpConnection(client, tools) :: connections
        } :> Task

    member _.GetAllTools() : ToolDef list =
        connections |> List.collect (fun c -> c.Tools)

    member _.CallToolAsync(name: string, args: Map<string, JsonNode>) : Task<string> =
        task {
            let conn =
                connections
                |> List.tryFind (fun c -> c.Tools |> List.exists (fun t -> t.Name = name))
            match conn with
            | None -> return $"""{{ "error": "tool_not_found: {name}" }}"""
            | Some c -> return! c.CallToolAsync(name, args)
        }

    interface IDisposable with
        member _.Dispose() =
            for c in connections do (c :> IDisposable).Dispose()
```

- [ ] **Step 3: Add `McpClient.fs` to compile list in orchestrator fsproj (after LlmClient.fs)**

- [ ] **Step 4: Build orchestrator**

```bash
dotnet build experiments/orchestrator/ 2>&1 | grep -E "^.*error"
```

Expected: no errors in McpClient.fs (other modules may still fail until Tasks 8–11).

---

### Task 8: Create Agent.fs

**Files:**
- Create: `experiments/orchestrator/Agent.fs`

- [ ] **Step 1: Write `experiments/orchestrator/Agent.fs`**

```fsharp
module TicTacToe.Orchestrator.Agent

open System
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.LlmClient
open TicTacToe.Orchestrator.McpClient

let private buildTranscript (agentId: string) (persona: Persona) (turns: LlmTurn list) (aborted: bool) : AgentTranscript =
    { AgentId = agentId; PersonaName = persona.Name; LlmTurns = turns; Aborted = aborted }

/// Execute one LLM turn and return updated turns list.
let private executeTurn
    (config: AgentConfig)
    (mcpClients: McpClientSet)
    (messages: JsonArray)
    (turnIndex: int)
    (currentTurns: LlmTurn list)
    : Task<LlmTurn list * bool> =  // returns (newTurns, keepGoing)
    task {
        let tools = mcpClients.GetAllTools()
        let! result =
            runTurn Anthropic config.Model config.Temperature
                (Some config.Persona.SystemPrompt) tools false messages

        match result with
        | Done(text, inp, out) ->
            let turn = {
                TurnIndex = turnIndex
                StopReason = "end_turn"
                InputTokens = inp
                OutputTokens = out
                ToolCalls = []
                TextOutput = if String.IsNullOrEmpty(text) then None else Some text
                Timestamp = DateTimeOffset.UtcNow
            }
            return (currentTurns @ [turn], false)

        | ToolCalls(calls, inp, out) ->
            appendAssistantToolUse Anthropic messages calls |> ignore

            let sw = Stopwatch()
            let toolCallRecords = System.Collections.Generic.List<ToolCallRecord>()
            let toolResults = System.Collections.Generic.List<string * string>()

            for call in calls do
                let args =
                    call.Input
                    |> Seq.map (fun kv -> kv.Key, kv.Value :> JsonNode)
                    |> Map.ofSeq

                sw.Restart()
                let! output = mcpClients.CallToolAsync(call.Name, args)
                sw.Stop()

                toolCallRecords.Add({
                    ToolName = call.Name
                    Input = call.Input.ToJsonString()
                    Output = Some output
                    Error = None
                    DurationMs = int sw.ElapsedMilliseconds
                    Timestamp = DateTimeOffset.UtcNow
                })
                toolResults.Add(call.Id, output)

            appendToolResults Anthropic messages (toolResults |> Seq.toList) |> ignore

            let turn = {
                TurnIndex = turnIndex
                StopReason = "tool_use"
                InputTokens = inp
                OutputTokens = out
                ToolCalls = toolCallRecords |> Seq.toList
                TextOutput = None
                Timestamp = DateTimeOffset.UtcNow
            }
            return (currentTurns @ [turn], true)
    }

let createAgent (config: AgentConfig) : MailboxProcessor<AgentMsg> =
    MailboxProcessor.Start(fun inbox ->
        let messages = JsonArray()
        appendUserText messages $"Here is a URL: {config.BaseUrl}" |> ignore

        let mutable turns: LlmTurn list = []
        let mutable aborted = false

        let rec runLoop (turnIndex: int) (clients: McpClientSet) =
            async {
                // Non-blocking check for Stop message between turns
                let! maybeStop = inbox.TryReceive(timeout = 0)
                match maybeStop with
                | Some (StopAgent reply) ->
                    (clients :> IDisposable).Dispose()
                    reply.Reply(buildTranscript config.Id config.Persona turns aborted)

                | Some (GetSnapshot reply) ->
                    reply.Reply({ AgentId = config.Id; TurnIndex = turnIndex; Aborted = aborted })
                    return! runLoop turnIndex clients

                | _ when turnIndex >= config.MaxTurns ->
                    aborted <- true
                    let! msg = inbox.Receive()
                    match msg with
                    | StopAgent reply ->
                        (clients :> IDisposable).Dispose()
                        reply.Reply(buildTranscript config.Id config.Persona turns true)
                    | _ -> ()

                | _ ->
                    let! (newTurns, keepGoing) =
                        executeTurn config clients messages turnIndex turns
                        |> Async.AwaitTask
                    turns <- newTurns
                    if keepGoing then
                        return! runLoop (turnIndex + 1) clients
                    else
                        // Agent is done; wait for explicit Stop
                        let! msg = inbox.Receive()
                        match msg with
                        | StopAgent reply ->
                            (clients :> IDisposable).Dispose()
                            reply.Reply(buildTranscript config.Id config.Persona turns aborted)
                        | _ -> ()
            }

        async {
            let! msg = inbox.Receive()
            match msg with
            | StartAgent ->
                let clients = McpClientSet(config.McpServers)
                do! clients.InitializeAsync() |> Async.AwaitTask
                return! runLoop 0 clients
            | StopAgent reply ->
                reply.Reply(buildTranscript config.Id config.Persona [] false)
            | GetSnapshot reply ->
                reply.Reply({ AgentId = config.Id; TurnIndex = 0; Aborted = false })
        })
```

- [ ] **Step 2: Add `Agent.fs` to compile list (after McpClient.fs)**

- [ ] **Step 3: Build**

```bash
dotnet build experiments/orchestrator/ 2>&1 | grep "error FS"
```

Expected: no errors in Agent.fs.

---

### Task 9: Create Orchestrator.fs

**Files:**
- Create: `experiments/orchestrator/Orchestrator.fs`

- [ ] **Step 1: Write `experiments/orchestrator/Orchestrator.fs`**

```fsharp
module TicTacToe.Orchestrator.Orchestrator

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics
open TicTacToe.Orchestrator.Persistence
open TicTacToe.Orchestrator.ServerLogTail
open TicTacToe.Orchestrator.ServerProcess
open TicTacToe.Orchestrator.Agent

let private makeAgentConfig (cell: CellSpec) (slot: int) (persona: Persona) (baseUrl: string) : AgentConfig =
    { Id = $"agent-{slot}"
      Persona = persona
      Model = cell.Model
      BaseUrl = baseUrl
      McpServers = cell.McpServers
      MaxTurns = cell.MaxTurnsPerAgent
      Temperature = cell.Temperature }

let private waitForGameOver (logPath: string) (maxWaitSeconds: int) : Async<bool> =
    async {
        let tail = startTail logPath
        let sw = Stopwatch.StartNew()
        let mutable found = false
        while not found && sw.Elapsed.TotalSeconds < float maxWaitSeconds do
            let events = tail.GetEvents()
            found <- events |> List.exists (function GameOver _ -> true | _ -> false)
            if not found then
                do! Async.Sleep(1000)
        return found
    }

let private runCell (repoRoot: string) (cell: CellSpec) : Async<CellResult> =
    async {
        let cellStart = DateTimeOffset.UtcNow
        printfn $"[cell] starting: {cell.Id}"

        let logPath = Path.Combine(repoRoot, "experiments", "results", cell.Id, "server-requests.jsonl")
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)) |> ignore

        // Start server (ERPC has no HTTP server — TicTacToe.Mcp is spawned per agent via MCP)
        let serverHandleOpt =
            if cell.Variant = ERPC then None
            else
                let handle = startServerForCell repoRoot cell |> Async.AwaitTask |> Async.RunSynchronously
                Some handle

        let baseUrl =
            serverHandleOpt
            |> Option.map (fun h -> h.BaseUrl)
            |> Option.defaultValue ""

        // Create 3 agents
        let (p1, p2, p3) = cell.Personas
        let agents =
            [1, p1; 2, p2; 3, p3]
            |> List.map (fun (slot, persona) ->
                createAgent (makeAgentConfig cell slot persona baseUrl))

        let sw = Stopwatch.StartNew()

        // Start all agents
        for agent in agents do agent.Post(StartAgent)

        // Wait up to 3 minutes for game_over
        let! gameOver = waitForGameOver logPath 180

        // Grace window: 5 seconds after game over
        if gameOver then do! Async.Sleep(5000)

        // Stop all agents and collect transcripts
        let transcriptList = System.Collections.Generic.List<AgentTranscript>()
        for agent in agents do
            let! t = agent.PostAndAsyncReply(fun r -> StopAgent r)
            transcriptList.Add(t)

        sw.Stop()
        let transcripts = transcriptList |> Seq.map (fun t -> t.AgentId, t) |> Map.ofSeq

        // Collect server log events
        let events = (startTail logPath).GetEvents()

        // Build role assignments (session_id → agentId mapping derived from cookie)
        // For now, use PlayerAssigned events with agentId heuristic
        // TODO: actual session tracking requires orchestrator to read auth cookie from agent
        let sessionMap = Map.empty  // placeholder; see Task 11 for full session tracking
        let roles = resolveRoles events sessionMap

        let durationSeconds = sw.Elapsed.TotalSeconds
        let allAbandoned = transcriptList |> Seq.forall (fun t -> t.Aborted)
        let metrics = computeCellMetrics cell.Id (transcriptList |> Seq.toList) roles sessionMap events durationSeconds cellStart

        let result = {
            CellSpec = cell
            Transcripts = transcripts
            ServerLogs = events
            Metrics = metrics
        }

        saveCell repoRoot cell.Id result

        serverHandleOpt |> Option.iter (fun h -> (h :> IDisposable).Dispose())

        printfn $"[cell] complete: {cell.Id} — {metrics.Outcome}"
        return result
    }

let runMatrix (repoRoot: string) (matrixName: string) (cells: CellSpec list) : Async<unit> =
    async {
        printfn $"[matrix] starting: {matrixName} ({cells.Length} cells)"
        let results = System.Collections.Generic.List<CellResult>()

        for cell in cells do
            let! result = runCell repoRoot cell
            results.Add(result)

        saveManifest repoRoot matrixName cells (results |> Seq.toList)
        printfn $"[matrix] complete: {matrixName}"
    }
```

- [ ] **Step 2: Add `Orchestrator.fs` to compile list (after Persistence.fs)**

---

### Task 10: Create Matrices/Smoke.fs

**Files:**
- Create: `experiments/orchestrator/Matrices/Smoke.fs`

- [ ] **Step 1: Create the `Matrices/` directory and write `Smoke.fs`**

```fsharp
module TicTacToe.Orchestrator.Matrices.Smoke

open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Personas

let private playwrightServer = {
    Name = "playwright"
    Command = "npx"
    Arguments = [| "@playwright/mcp"; "--headless" |]
}

let private mcpServer = {
    Name = "tictactoe-mcp"
    Command = "dotnet"
    Arguments = [| "run"; "--project"; "src/TicTacToe.Mcp/" |]
}

let private cell id variant p1 p2 p3 mcpServers = {
    Id = id
    Variant = variant
    Personas = (p1, p2, p3)
    Model = "google/gemma-4-e4b"
    InitialGames = 1
    MaxGames = 1
    MaxTurnsPerAgent = 50
    McpServers = mcpServers
    Temperature = 0.0
}

let smoke : CellSpec list = [
    cell "smoke-proto-bbb"  Proto  beginner beginner beginner [playwrightServer]
    cell "smoke-simple-bbb" Simple beginner beginner beginner [playwrightServer]
    cell "smoke-erpc-bbb"   ERPC   beginner beginner beginner [mcpServer]
    cell "smoke-erpc-bbc"   ERPC   beginner beginner chaos    [mcpServer]
    cell "smoke-proto-bbc"  Proto  beginner beginner chaos    [playwrightServer]
    cell "smoke-simple-bbc" Simple beginner beginner chaos    [playwrightServer]
]
```

- [ ] **Step 2: Add `Matrices/Smoke.fs` to compile list (last before Program.fs)**

```xml
<Compile Include="Matrices/Smoke.fs" />
```

---

### Task 11: Update Program.fs and fsproj; delete obsolete files

**Files:**
- Rewrite: `experiments/orchestrator/Program.fs`
- Modify: `experiments/orchestrator/TicTacToe.Orchestrator.fsproj`
- Delete: `HttpAgent.fs`, `RpcAgent.fs`, `Classifier.fs`, `Runner.fs`
- Delete: `test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs`

- [ ] **Step 1: Rewrite `experiments/orchestrator/Program.fs`**

```fsharp
module TicTacToe.Orchestrator.Program

open System
open System.IO
open TicTacToe.Orchestrator.Orchestrator
open TicTacToe.Orchestrator.Matrices.Smoke

[<EntryPoint>]
let main args =
    let repoRoot =
        // Walk up from cwd to find the repo root (contains TicTacToe.sln)
        let rec find (dir: string) =
            if File.Exists(Path.Combine(dir, "TicTacToe.sln")) then dir
            else
                let parent = Directory.GetParent(dir)
                if parent = null then failwith "Cannot find repo root (TicTacToe.sln not found)"
                find parent.FullName
        find (Directory.GetCurrentDirectory())

    let matrix, cells =
        match args with
        | [| "smoke" |] -> "smoke", smoke
        | [| name |] -> failwithf "Unknown matrix: %s. Available: smoke" name
        | _ -> failwith "Usage: dotnet run --project experiments/orchestrator/ -- <matrix-name>"

    runMatrix repoRoot matrix cells |> Async.RunSynchronously
    0
```

- [ ] **Step 2: Update `TicTacToe.Orchestrator.fsproj` compile list**

Replace the entire `<ItemGroup>` compile list with the new order:

```xml
<ItemGroup>
    <Compile Include="Types.fs" />
    <Compile Include="Personas.fs" />
    <Compile Include="LlmClient.fs" />
    <Compile Include="ServerProcess.fs" />
    <Compile Include="ServerLogTail.fs" />
    <Compile Include="McpClient.fs" />
    <Compile Include="Metrics.fs" />
    <Compile Include="Persistence.fs" />
    <Compile Include="Agent.fs" />
    <Compile Include="Orchestrator.fs" />
    <Compile Include="Matrices/Smoke.fs" />
    <Compile Include="Program.fs" />
</ItemGroup>
```

- [ ] **Step 3: Delete obsolete files**

```bash
git rm experiments/orchestrator/HttpAgent.fs
git rm experiments/orchestrator/RpcAgent.fs
git rm experiments/orchestrator/Classifier.fs
git rm experiments/orchestrator/Runner.fs
git rm test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs
```

- [ ] **Step 4: Full build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 5: Run all orchestrator unit tests**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: all tests pass (Metrics, ServerLogTail, Persistence).

- [ ] **Step 6: Commit**

```bash
git add experiments/orchestrator/ test/TicTacToe.Orchestrator.Tests/
git commit -m "feat(orchestrator): complete three-agent redesign — Agent, Orchestrator, Smoke matrix"
```

---

### Task 12: Smoke run acceptance test

- [ ] **Step 1: Prerequisites**

Verify:
- LM Studio is running with `google/gemma-4-e4b` loaded on the Anthropic endpoint
- `dotnet build src/TicTacToe.Mcp/` succeeds
- `TicTacToe.Web.Simple` starts on port 5328 with `TICTACTOE_REQUEST_LOG_PATH` set

- [ ] **Step 2: Run the smoke matrix**

```bash
cd /path/to/repo
dotnet run --project experiments/orchestrator/ -- smoke
```

Expected: each cell prints start/complete log lines; `experiments/results/smoke/manifest.json` exists after completion.

- [ ] **Step 3: Verify outputs**

```bash
# Check all 6 cells have required files
for cell in smoke-proto-bbb smoke-simple-bbb smoke-erpc-bbb smoke-erpc-bbc smoke-proto-bbc smoke-simple-bbc; do
  dir="experiments/results/$cell"
  echo "=== $cell ==="
  ls "$dir" 2>/dev/null || echo "MISSING"
  cat "$dir/metrics.json" 2>/dev/null | grep '"outcome"'
done
```

Expected: each cell directory contains `transcripts/`, `metrics.json`, `cell-spec.json`. Outcome is one of `x_wins`, `o_wins`, `draw`, `abandoned`.

- [ ] **Step 4: Final commit**

```bash
git add experiments/results/  # add any results you want to track
git commit -m "test(smoke): G0 smoke matrix run — three-agent redesign validation"
```
