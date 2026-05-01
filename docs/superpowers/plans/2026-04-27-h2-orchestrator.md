# H2 Orchestrator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a CLI tool that drives a Claude agent against the tic-tac-toe server, captures full HTTP (or MCP-tool) transcripts, and outputs per-game RPVA / invalid-rate / abandon / token metrics as JSON.

**Architecture:** A new F# console project `experiments/orchestrator/` defines a `RunConfig` from CLI flags, launches the target server at `--commit` via `dotnet publish` + subprocess, then runs N games by giving Claude either an `http_request` tool (E0/E1) or four game-domain tools (E_RPC), cycling through the Messages API tool-use loop, recording every request/response, and emitting a structured JSON output file. A companion NUnit test project covers the classifier and metrics units deterministically.

**Tech Stack:** F# .NET 10.0, `System.Net.Http` (Anthropic API + HTTP agent), `System.Text.Json`, `System.Diagnostics.Process` (server subprocess + git worktree), NUnit (unit tests). No third-party SDK for Claude — direct `POST /v1/messages` with `x-api-key` header.

---

## File Map

| File | Responsibility |
|---|---|
| `experiments/orchestrator/TicTacToe.Orchestrator.fsproj` | Project definition |
| `experiments/orchestrator/Types.fs` | All domain types: `RunConfig`, `TranscriptEntry`, `GameRecord`, `RunOutput`, `OutcomeTag`, `StrategyTag` |
| `experiments/orchestrator/Classifier.fs` | Outcome + strategy classification logic |
| `experiments/orchestrator/Metrics.fs` | RPVA, invalid-rate, abandon, token aggregation |
| `experiments/orchestrator/AnthropicClient.fs` | Direct-HTTP Messages API client: request/response types, tool-use cycle |
| `experiments/orchestrator/HttpAgent.fs` | E0/E1 game loop: exposes `http_request` tool, drives Claude, records `HttpEntry` transcript |
| `experiments/orchestrator/RpcAgent.fs` | E_RPC game loop: exposes four game tools backed by `TicTacToe.Engine`, drives Claude, records `ToolEntry` transcript |
| `experiments/orchestrator/ServerProcess.fs` | Build server at `--commit` via `git worktree` + `dotnet publish`, launch subprocess on dynamic port, teardown |
| `experiments/orchestrator/Runner.fs` | Orchestrate N games, aggregate metrics, produce `RunOutput` |
| `experiments/orchestrator/Program.fs` | CLI arg parsing, top-level entry point, JSON output |
| `test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj` | NUnit test project |
| `test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs` | Unit tests for outcome + strategy classification |
| `test/TicTacToe.Orchestrator.Tests/MetricsTests.fs` | Unit tests for RPVA/invalid-rate/aggregate computation (includes AT4 canned transcript) |

---

## Task 1: Project Scaffold

**Files:**
- Create: `experiments/orchestrator/TicTacToe.Orchestrator.fsproj`
- Create: `test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj`
- Modify: `TicTacToe.sln` (add both projects)

- [ ] **Step 1: Create the orchestrator fsproj**

```xml
<!-- experiments/orchestrator/TicTacToe.Orchestrator.fsproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <AssemblyName>TicTacToe.Orchestrator</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="Types.fs" />
    <Compile Include="Classifier.fs" />
    <Compile Include="Metrics.fs" />
    <Compile Include="AnthropicClient.fs" />
    <Compile Include="HttpAgent.fs" />
    <Compile Include="RpcAgent.fs" />
    <Compile Include="ServerProcess.fs" />
    <Compile Include="Runner.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/TicTacToe.Engine/TicTacToe.Engine.fsproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="9.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test fsproj**

```xml
<!-- test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="ClassifierTests.fs" />
    <Compile Include="MetricsTests.fs" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../experiments/orchestrator/TicTacToe.Orchestrator.fsproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="NUnit" Version="4.*" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add both projects to the solution**

```bash
cd C:/Users/ryanr/Code/tic-tac-toe
dotnet sln add experiments/orchestrator/TicTacToe.Orchestrator.fsproj
dotnet sln add test/TicTacToe.Orchestrator.Tests/TicTacToe.Orchestrator.Tests.fsproj
```

Expected: `Project added to the solution.` (×2)

- [ ] **Step 4: Create stub source files so the project builds**

Create `experiments/orchestrator/Types.fs`:
```fsharp
module TicTacToe.Orchestrator.Types
// stubs — filled in Task 2
```

Create `experiments/orchestrator/Classifier.fs`:
```fsharp
module TicTacToe.Orchestrator.Classifier
```

Create `experiments/orchestrator/Metrics.fs`:
```fsharp
module TicTacToe.Orchestrator.Metrics
```

Create `experiments/orchestrator/AnthropicClient.fs`:
```fsharp
module TicTacToe.Orchestrator.AnthropicClient
```

Create `experiments/orchestrator/HttpAgent.fs`:
```fsharp
module TicTacToe.Orchestrator.HttpAgent
```

Create `experiments/orchestrator/RpcAgent.fs`:
```fsharp
module TicTacToe.Orchestrator.RpcAgent
```

Create `experiments/orchestrator/ServerProcess.fs`:
```fsharp
module TicTacToe.Orchestrator.ServerProcess
```

Create `experiments/orchestrator/Runner.fs`:
```fsharp
module TicTacToe.Orchestrator.Runner
```

Create `experiments/orchestrator/Program.fs`:
```fsharp
module TicTacToe.Orchestrator.Program

[<EntryPoint>]
let main _ =
    printfn "orchestrator stub"
    0
```

Create `test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs`:
```fsharp
module TicTacToe.Orchestrator.Tests.ClassifierTests
open NUnit.Framework

[<TestFixture>]
type ClassifierTests() =
    [<Test>]
    member _.``stub``() = Assert.Pass()
```

Create `test/TicTacToe.Orchestrator.Tests/MetricsTests.fs`:
```fsharp
module TicTacToe.Orchestrator.Tests.MetricsTests
open NUnit.Framework

[<TestFixture>]
type MetricsTests() =
    [<Test>]
    member _.``stub``() = Assert.Pass()
```

- [ ] **Step 5: Verify build**

```bash
dotnet build
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: `Build succeeded`, `Passed: 2`

- [ ] **Step 6: Commit**

```bash
git add experiments/orchestrator/ test/TicTacToe.Orchestrator.Tests/ TicTacToe.sln
git commit -m "feat(H2): scaffold orchestrator project and test project"
```

---

## Task 2: Domain Types

**Files:**
- Modify: `experiments/orchestrator/Types.fs`

- [ ] **Step 1: Replace stub with full types**

```fsharp
module TicTacToe.Orchestrator.Types

open System.Text.Json
open System.Text.Json.Serialization

// ── CLI config ───────────────────────────────────────────────────────────────

type Variant = Proto | Simple

type ModelId = Haiku | Sonnet | Opus

type Persona = Beginner | Expert | Chaos

type Setup = E0 | E1 | ERPC

type RunConfig = {
    Commit: string
    Variant: Variant
    Model: ModelId
    Persona: Persona
    Setup: Setup
    Games: int
    Output: string
    Temperature: float
}

// ── Transcript ───────────────────────────────────────────────────────────────

[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type OutcomeTag =
    | ValidAction
    | InvalidAction
    | Discovery
    | Retry
    | Abandoned

[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type StrategyTag =
    | HtmlFollow
    | SpecFollow
    | BlindPost
    | RetryStrategy  // "retry" in output JSON

type HttpEntry = {
    [<JsonPropertyName("turn")>] Turn: int
    [<JsonPropertyName("method")>] Method: string
    [<JsonPropertyName("url")>] Url: string
    [<JsonPropertyName("request_headers")>] RequestHeaders: Map<string, string>
    [<JsonPropertyName("request_body")>] RequestBody: string option
    [<JsonPropertyName("status")>] StatusCode: int
    [<JsonPropertyName("response_headers")>] ResponseHeaders: Map<string, string>
    [<JsonPropertyName("response_body")>] ResponseBody: string
    [<JsonPropertyName("outcome")>] Outcome: OutcomeTag
    [<JsonPropertyName("strategy")>] Strategy: StrategyTag
}

type ToolEntry = {
    [<JsonPropertyName("turn")>] Turn: int
    [<JsonPropertyName("tool_use_id")>] ToolUseId: string
    [<JsonPropertyName("tool_name")>] ToolName: string
    [<JsonPropertyName("input")>] Input: string
    [<JsonPropertyName("output")>] Output: string
    [<JsonPropertyName("outcome")>] Outcome: OutcomeTag
}

[<JsonConverter(typeof<TranscriptEntryConverter>)>]
type TranscriptEntry =
    | Http of HttpEntry
    | Tool of ToolEntry

// Custom converter so Http entries serialize as the HttpEntry fields directly
and TranscriptEntryConverter() =
    inherit JsonConverter<TranscriptEntry>()
    override _.Write(writer, value, opts) =
        match value with
        | Http e -> JsonSerializer.Serialize(writer, e, opts)
        | Tool e -> JsonSerializer.Serialize(writer, e, opts)
    override _.Read(_, _, _) = failwith "not used"

// ── Metrics ──────────────────────────────────────────────────────────────────

type GameMetrics = {
    [<JsonPropertyName("rpva")>] Rpva: float
    [<JsonPropertyName("invalid_rate")>] InvalidRate: float
    [<JsonPropertyName("abandoned")>] Abandoned: bool
    [<JsonPropertyName("tokens")>] Tokens: int
}

type GameRecord = {
    [<JsonPropertyName("transcript")>] Transcript: TranscriptEntry list
    [<JsonPropertyName("metrics")>] Metrics: GameMetrics
}

type CellId = {
    [<JsonPropertyName("commit")>] Commit: string
    [<JsonPropertyName("variant")>] Variant: string
    [<JsonPropertyName("model")>] Model: string
    [<JsonPropertyName("persona")>] Persona: string
    [<JsonPropertyName("setup")>] Setup: string
}

type Aggregate = {
    [<JsonPropertyName("rpva")>] Rpva: float
    [<JsonPropertyName("invalid_rate")>] InvalidRate: float
    [<JsonPropertyName("abandon_rate")>] AbandonRate: float
    [<JsonPropertyName("tokens")>] Tokens: float
}

type RunOutput = {
    [<JsonPropertyName("cell")>] Cell: CellId
    [<JsonPropertyName("games")>] Games: GameRecord list
    [<JsonPropertyName("aggregate")>] Aggregate: Aggregate
}

// ── Helpers ──────────────────────────────────────────────────────────────────

module ModelId =
    let toApiString = function
        | Haiku -> "claude-haiku-4-5-20251001"
        | Sonnet -> "claude-sonnet-4-6"
        | Opus -> "claude-opus-4-7"
    let toString = function Haiku -> "haiku" | Sonnet -> "sonnet" | Opus -> "opus"

module Variant =
    let toString = function Proto -> "proto" | Simple -> "simple"
    let defaultPort = function Proto -> 5228 | Simple -> 5328
    let projectPath = function
        | Proto -> "src/TicTacToe.Web/TicTacToe.Web.fsproj"
        | Simple -> "src/TicTacToe.Web.Simple/TicTacToe.Web.Simple.fsproj"

module Persona =
    let toString = function Beginner -> "beginner" | Expert -> "expert" | Chaos -> "chaos"

module Setup =
    let toString = function E0 -> "E0" | E1 -> "E1" | ERPC -> "E_RPC"
```

- [ ] **Step 2: Build to verify types compile**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add experiments/orchestrator/Types.fs
git commit -m "feat(H2): define domain types (RunConfig, TranscriptEntry, RunOutput)"
```

---

## Task 3: Classifier

**Files:**
- Modify: `experiments/orchestrator/Classifier.fs`
- Modify: `test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs`

The classifier determines `OutcomeTag` from HTTP status code and `StrategyTag` from whether the requested URL appeared in prior responses.

- [ ] **Step 1: Write failing classifier tests**

```fsharp
// test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs
module TicTacToe.Orchestrator.Tests.ClassifierTests

open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Classifier

[<TestFixture>]
type OutcomeTests() =

    [<Test>]
    member _.``200 response to /arenas POST is ValidAction``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123" 200
        Assert.That(outcome, Is.EqualTo(ValidAction))

    [<Test>]
    member _.``200 response to GET /arenas is Discovery``() =
        let outcome = classifyOutcome "GET" "/" 200
        Assert.That(outcome, Is.EqualTo(Discovery))

    [<Test>]
    member _.``200 GET /arenas/id is Discovery not ValidAction``() =
        let outcome = classifyOutcome "GET" "/arenas/abc123" 200
        Assert.That(outcome, Is.EqualTo(Discovery))

    [<Test>]
    member _.``422 response is InvalidAction``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123" 422
        Assert.That(outcome, Is.EqualTo(InvalidAction))

    [<Test>]
    member _.``400 response is InvalidAction``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123" 400
        Assert.That(outcome, Is.EqualTo(InvalidAction))

    [<Test>]
    member _.``POST to /arenas/id/restart is ValidAction on 200``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123/restart" 200
        Assert.That(outcome, Is.EqualTo(ValidAction))

[<TestFixture>]
type StrategyTests() =

    [<Test>]
    member _.``URL found in prior response body is HtmlFollow``() =
        let priorBodies = ["/arenas/abc123"]
        let strategy = classifyStrategy "POST" "/arenas/abc123" priorBodies false
        Assert.That(strategy, Is.EqualTo(HtmlFollow))

    [<Test>]
    member _.``URL found in OpenAPI doc is SpecFollow``() =
        let priorBodies = []
        let strategy = classifyStrategy "GET" "/arenas/abc123" priorBodies true
        Assert.That(strategy, Is.EqualTo(SpecFollow))

    [<Test>]
    member _.``URL not in any prior response is BlindPost``() =
        let priorBodies = []
        let strategy = classifyStrategy "POST" "/arenas/xyz999" priorBodies false
        Assert.That(strategy, Is.EqualTo(BlindPost))

    [<Test>]
    member _.``URL in prior body takes precedence over spec``() =
        let priorBodies = ["/arenas/abc123"]
        let strategy = classifyStrategy "POST" "/arenas/abc123" priorBodies true
        Assert.That(strategy, Is.EqualTo(HtmlFollow))
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/ --filter "ClassifierTests"
```

Expected: FAIL — `classifyOutcome` and `classifyStrategy` not defined.

- [ ] **Step 3: Implement Classifier.fs**

```fsharp
module TicTacToe.Orchestrator.Classifier

open TicTacToe.Orchestrator.Types

/// Classify an HTTP response as an outcome tag.
/// ValidAction: state-mutating request (POST/DELETE to a resource path) that succeeded.
/// Discovery: read-only request (GET) — including 200 GET /openapi/v1.json, GET /.
/// InvalidAction: 4xx response to any request.
/// Abandoned: not set here — set by the game loop when max turns hit.
let classifyOutcome (method: string) (url: string) (statusCode: int) : OutcomeTag =
    if statusCode >= 400 then InvalidAction
    elif method = "GET" then Discovery
    else ValidAction   // 2xx POST/DELETE = state mutation

/// Classify what strategy the agent used to produce this URL.
/// priorResponseBodies: list of all response body strings received so far in this game.
/// urlInOpenApiDoc: true if the URL path matches a path in the OpenAPI document fetched during this game.
let classifyStrategy (method: string) (url: string) (priorResponseBodies: string list) (urlInOpenApiDoc: bool) : StrategyTag =
    let appearsInBody = priorResponseBodies |> List.exists (fun body -> body.Contains(url))
    if appearsInBody then HtmlFollow
    elif urlInOpenApiDoc then SpecFollow
    else BlindPost
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/ --filter "ClassifierTests"
```

Expected: `Passed: 9`

- [ ] **Step 5: Commit**

```bash
git add experiments/orchestrator/Classifier.fs test/TicTacToe.Orchestrator.Tests/ClassifierTests.fs
git commit -m "feat(H2): add outcome and strategy classifier with unit tests"
```

---

## Task 4: Metrics

**Files:**
- Modify: `experiments/orchestrator/Metrics.fs`
- Modify: `test/TicTacToe.Orchestrator.Tests/MetricsTests.fs`

This task also implements AT4: a canned transcript → RPVA = 2.0, invalid_rate = 0.33.

- [ ] **Step 1: Write failing metrics tests**

```fsharp
// test/TicTacToe.Orchestrator.Tests/MetricsTests.fs
module TicTacToe.Orchestrator.Tests.MetricsTests

open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics

let private makeHttp outcome =
    Http { Turn = 0; Method = "GET"; Url = "/"; RequestHeaders = Map.empty; RequestBody = None
           StatusCode = 200; ResponseHeaders = Map.empty; ResponseBody = ""
           Outcome = outcome; Strategy = BlindPost }

[<TestFixture>]
type MetricsTests() =

    // AT4: 3 valid, 2 invalid, 1 discovery → RPVA = (3+2+1)/3 = 2.0; invalid_rate = 2/6 = 0.33
    [<Test>]
    member _.``AT4 canned transcript: RPVA 2.0 invalid_rate 0.33``() =
        let transcript = [
            makeHttp ValidAction
            makeHttp ValidAction
            makeHttp ValidAction
            makeHttp InvalidAction
            makeHttp InvalidAction
            makeHttp Discovery
        ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Rpva, Is.EqualTo(2.0).Within(0.001))
        Assert.That(metrics.InvalidRate, Is.EqualTo(0.333).Within(0.001))
        Assert.That(metrics.Abandoned, Is.False)

    [<Test>]
    member _.``RPVA floor is 2.0 when one valid action and one discovery``() =
        let transcript = [ makeHttp Discovery; makeHttp ValidAction ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Rpva, Is.EqualTo(2.0).Within(0.001))

    [<Test>]
    member _.``Abandoned is true when maxTurns is flagged``() =
        let transcript = [ makeHttp Discovery ]
        let metrics = computeMetrics transcript 0
        // With no ValidAction outcomes the game abandoned
        Assert.That(metrics.Abandoned, Is.True)

    [<Test>]
    member _.``Zero valid actions gives RPVA of positive infinity, clamped to max float``() =
        let transcript = [ makeHttp InvalidAction; makeHttp Discovery ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Rpva, Is.EqualTo(System.Double.MaxValue))

    [<Test>]
    member _.``aggregate averages RPVA, invalid_rate, tokens across games``() =
        let g1 = { Transcript = [makeHttp ValidAction]; Metrics = { Rpva = 2.0; InvalidRate = 0.0; Abandoned = false; Tokens = 100 } }
        let g2 = { Transcript = [makeHttp ValidAction]; Metrics = { Rpva = 4.0; InvalidRate = 0.5; Abandoned = false; Tokens = 200 } }
        let agg = aggregate [g1; g2]
        Assert.That(agg.Rpva, Is.EqualTo(3.0).Within(0.001))
        Assert.That(agg.InvalidRate, Is.EqualTo(0.25).Within(0.001))
        Assert.That(agg.AbandonRate, Is.EqualTo(0.0).Within(0.001))
        Assert.That(agg.Tokens, Is.EqualTo(150.0).Within(0.001))
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/ --filter "MetricsTests"
```

Expected: FAIL — `computeMetrics` and `aggregate` not defined.

- [ ] **Step 3: Implement Metrics.fs**

```fsharp
module TicTacToe.Orchestrator.Metrics

open TicTacToe.Orchestrator.Types

/// Compute per-game metrics from a transcript.
/// totalTokens: total LLM context tokens consumed across all turns in this game.
let computeMetrics (transcript: TranscriptEntry list) (totalTokens: int) : GameMetrics =
    let outcomes =
        transcript |> List.map (function
            | Http e -> e.Outcome
            | Tool e -> e.Outcome)

    let total = outcomes |> List.length |> float
    let validCount = outcomes |> List.filter ((=) ValidAction) |> List.length
    let invalidCount = outcomes |> List.filter ((=) InvalidAction) |> List.length

    let rpva =
        if validCount = 0 then System.Double.MaxValue
        else total / float validCount

    let invalidRate =
        if total = 0.0 then 0.0
        else float invalidCount / total

    let abandoned = validCount = 0

    { Rpva = rpva; InvalidRate = invalidRate; Abandoned = abandoned; Tokens = totalTokens }

/// Aggregate metrics across all games in a run.
let aggregate (games: GameRecord list) : Aggregate =
    let n = float (List.length games)
    if n = 0.0 then
        { Rpva = 0.0; InvalidRate = 0.0; AbandonRate = 0.0; Tokens = 0.0 }
    else
        let rpvas = games |> List.map (fun g -> g.Metrics.Rpva)
        // Exclude MaxValue (abandoned) from RPVA average — those contribute to abandon_rate
        let validRpvas = rpvas |> List.filter (fun r -> r < System.Double.MaxValue)
        let rpva =
            if validRpvas.IsEmpty then System.Double.MaxValue
            else validRpvas |> List.average
        let invalidRate = games |> List.averageBy (fun g -> g.Metrics.InvalidRate)
        let abandonRate = games |> List.filter (fun g -> g.Metrics.Abandoned) |> List.length |> fun c -> float c / n
        let tokens = games |> List.averageBy (fun g -> float g.Metrics.Tokens)
        { Rpva = rpva; InvalidRate = invalidRate; AbandonRate = abandonRate; Tokens = tokens }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/ --filter "MetricsTests"
```

Expected: `Passed: 5`

- [ ] **Step 5: Commit**

```bash
git add experiments/orchestrator/Metrics.fs test/TicTacToe.Orchestrator.Tests/MetricsTests.fs
git commit -m "feat(H2): add metrics computation with AT4 canned-transcript test"
```

---

## Task 5: Anthropic API Client

**Files:**
- Modify: `experiments/orchestrator/AnthropicClient.fs`

This module calls `POST https://api.anthropic.com/v1/messages` directly via `HttpClient`. It cycles the tool-use loop: send messages → handle tool_use responses → append tool_results → repeat until `stop_reason = "end_turn"` or no tool_use content.

Reads `ANTHROPIC_API_KEY` from the environment.

- [ ] **Step 1: Implement AnthropicClient.fs**

```fsharp
module TicTacToe.Orchestrator.AnthropicClient

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

let private client = new HttpClient(BaseAddress = Uri("https://api.anthropic.com"))

let private apiKey () =
    match Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") with
    | null | "" -> failwith "ANTHROPIC_API_KEY environment variable not set"
    | k -> k

// ── Tool definition ──────────────────────────────────────────────────────────

type ToolDef = {
    Name: string
    Description: string
    InputSchema: JsonNode
}

// ── Request/response helpers ─────────────────────────────────────────────────

/// Build a JsonNode array for the messages list.
/// Each element is { "role": "user"|"assistant", "content": ... }
let private buildRequest (model: string) (temperature: float) (systemPrompt: string option) (tools: ToolDef list) (messages: JsonArray) : JsonObject =
    let req = JsonObject()
    req["model"] <- JsonValue.Create(model)
    req["max_tokens"] <- JsonValue.Create(4096)
    req["temperature"] <- JsonValue.Create(temperature)

    match systemPrompt with
    | Some prompt ->
        // Cache the system prompt for efficiency across turns
        let sysArr = JsonArray()
        let sysBlock = JsonObject()
        sysBlock["type"] <- JsonValue.Create("text")
        sysBlock["text"] <- JsonValue.Create(prompt)
        let cc = JsonObject()
        cc["type"] <- JsonValue.Create("ephemeral")
        sysBlock["cache_control"] <- cc
        sysArr.Add(sysBlock)
        req["system"] <- sysArr
    | None -> ()

    if not tools.IsEmpty then
        let toolArr = JsonArray()
        for t in tools do
            let td = JsonObject()
            td["name"] <- JsonValue.Create(t.Name)
            td["description"] <- JsonValue.Create(t.Description)
            td["input_schema"] <- t.InputSchema.DeepClone()
            toolArr.Add(td)
        req["tools"] <- toolArr

    req["messages"] <- messages.DeepClone() :?> JsonArray
    req

/// POST /v1/messages and return the raw response JsonObject.
let private postMessages (req: JsonObject) : Task<JsonObject> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        request.Headers.Add("x-api-key", apiKey())
        request.Headers.Add("anthropic-version", "2023-06-01")
        request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31")
        request.Content <- JsonContent.Create(req)
        let! response = client.SendAsync(request)
        let! json = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            failwithf "Anthropic API error %d: %s" (int response.StatusCode) json
        return JsonNode.Parse(json) :?> JsonObject
    }

// ── Tool call record ─────────────────────────────────────────────────────────

type ToolCall = {
    Id: string
    Name: string
    Input: JsonObject
}

type TurnResult =
    | ToolCalls of calls: ToolCall list * inputTokens: int * outputTokens: int
    | Done of text: string * inputTokens: int * outputTokens: int

// ── Core: run one API turn ───────────────────────────────────────────────────

/// Send current message history, return either tool calls or a final text response.
let runTurn (model: string) (temperature: float) (systemPrompt: string option) (tools: ToolDef list) (messages: JsonArray) : Task<TurnResult> =
    task {
        let req = buildRequest model temperature systemPrompt tools messages
        let! resp = postMessages req

        let usage = resp["usage"] :?> JsonObject
        let inputTokens = usage["input_tokens"].GetValue<int>()
        let outputTokens = usage["output_tokens"].GetValue<int>()

        let content = resp["content"] :?> JsonArray
        let toolUses =
            content
            |> Seq.cast<JsonNode>
            |> Seq.filter (fun n -> (n :?> JsonObject)["type"].GetValue<string>() = "tool_use")
            |> Seq.map (fun n ->
                let o = n :?> JsonObject
                { Id = o["id"].GetValue<string>()
                  Name = o["name"].GetValue<string>()
                  Input = o["input"] :?> JsonObject })
            |> Seq.toList

        if toolUses.IsEmpty then
            let text =
                content
                |> Seq.cast<JsonNode>
                |> Seq.tryFind (fun n -> (n :?> JsonObject)["type"].GetValue<string>() = "text")
                |> Option.map (fun n -> (n :?> JsonObject)["text"].GetValue<string>())
                |> Option.defaultValue ""
            return Done(text, inputTokens, outputTokens)
        else
            return ToolCalls(toolUses, inputTokens, outputTokens)
    }

// ── Message history helpers ───────────────────────────────────────────────────

/// Append an assistant turn (with tool_use content blocks) to message history.
let appendAssistantToolUse (messages: JsonArray) (calls: ToolCall list) : JsonArray =
    let msg = JsonObject()
    msg["role"] <- JsonValue.Create("assistant")
    let content = JsonArray()
    for call in calls do
        let block = JsonObject()
        block["type"] <- JsonValue.Create("tool_use")
        block["id"] <- JsonValue.Create(call.Id)
        block["name"] <- JsonValue.Create(call.Name)
        block["input"] <- call.Input.DeepClone()
        content.Add(block)
    msg["content"] <- content
    messages.Add(msg.DeepClone())
    messages

/// Append a user turn with tool_result content blocks.
let appendToolResults (messages: JsonArray) (results: (string * string) list) : JsonArray =
    let msg = JsonObject()
    msg["role"] <- JsonValue.Create("user")
    let content = JsonArray()
    for (id, result) in results do
        let block = JsonObject()
        block["type"] <- JsonValue.Create("tool_result")
        block["tool_use_id"] <- JsonValue.Create(id)
        block["content"] <- JsonValue.Create(result)
        content.Add(block)
    msg["content"] <- content
    messages.Add(msg.DeepClone())
    messages

/// Append a simple user text message.
let appendUserText (messages: JsonArray) (text: string) : JsonArray =
    let msg = JsonObject()
    msg["role"] <- JsonValue.Create("user")
    msg["content"] <- JsonValue.Create(text)
    messages.Add(msg.DeepClone())
    messages
```

- [ ] **Step 2: Verify build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add experiments/orchestrator/AnthropicClient.fs
git commit -m "feat(H2): implement direct-HTTP Anthropic Messages API client with tool-use cycle"
```

---

## Task 6: HTTP Agent (E0/E1)

**Files:**
- Modify: `experiments/orchestrator/HttpAgent.fs`

The HTTP agent gives Claude a single `http_request` tool. Claude calls it to interact with the tic-tac-toe server. The agent loop runs until Claude stops issuing tool calls (game over) or 50 turns are exhausted.

Transcript recording happens here: every `http_request` tool call becomes an `HttpEntry` with classified outcome and strategy.

- [ ] **Step 1: Implement HttpAgent.fs**

```fsharp
module TicTacToe.Orchestrator.HttpAgent

open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.AnthropicClient
open TicTacToe.Orchestrator.Classifier

let private maxTurns = 50

// ── http_request tool definition ────────────────────────────────────────────

let private httpRequestTool : ToolDef =
    { Name = "http_request"
      Description = "Make an HTTP request to the tic-tac-toe server. Follow links in responses to discover available actions. Read response bodies carefully — they contain affordances (links, forms, JSON fields) that tell you what to do next."
      InputSchema =
        JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "method": { "type": "string", "enum": ["GET","POST","DELETE"] },
            "url": { "type": "string", "description": "Full URL, e.g. http://localhost:5228/arenas" },
            "headers": { "type": "object", "description": "Optional headers as string key-value pairs" },
            "body": { "type": "string", "description": "Request body (URL-encoded form data or JSON)" }
          },
          "required": ["method","url"]
        }""") }

// ── HTTP execution ───────────────────────────────────────────────────────────

let private executeHttp (httpClient: HttpClient) (call: ToolCall) : Task<int * Map<string,string> * string> =
    task {
        let input = call.Input
        let method = input["method"].GetValue<string>()
        let url = input["url"].GetValue<string>()

        use req = new HttpRequestMessage(
            Method = HttpMethod(method),
            RequestUri = System.Uri(url))

        // Apply caller-specified headers
        match input.TryGetPropertyValue("headers") with
        | true, hdrs ->
            for prop in (hdrs :?> JsonObject) do
                req.Headers.TryAddWithoutValidation(prop.Key, prop.Value.GetValue<string>()) |> ignore
        | _ -> ()

        // Body
        match input.TryGetPropertyValue("body") with
        | true, body when body <> null ->
            let bodyStr = body.GetValue<string>()
            req.Content <- new StringContent(bodyStr, System.Text.Encoding.UTF8)
            // honour Content-Type if provided, else default to form-encoded
            if not (req.Headers.Contains("Content-Type")) then
                req.Content.Headers.ContentType <- MediaTypeHeaderValue("application/x-www-form-urlencoded")
        | _ -> ()

        let! resp = httpClient.SendAsync(req)
        let statusCode = int resp.StatusCode
        let responseHeaders =
            resp.Headers
            |> Seq.map (fun kvp -> kvp.Key, String.concat "," kvp.Value)
            |> Map.ofSeq
        let! responseBody = resp.Content.ReadAsStringAsync()
        return (statusCode, responseHeaders, responseBody)
    }

// ── Game loop ────────────────────────────────────────────────────────────────

/// Run one game with E0 or E1 setup. Returns (transcript, totalTokens).
let runGame
    (httpClient: HttpClient)
    (model: string)
    (temperature: float)
    (systemPrompt: string option)
    (baseUrl: string)
    : Task<TranscriptEntry list * int> =
    task {
        let messages = JsonArray()
        AnthropicClient.appendUserText messages (
            match systemPrompt with
            | Some _ -> $"Here is a URL: {baseUrl}"  // E1 prompt from beginner.md
            | None -> baseUrl                          // E0: bare URL
        ) |> ignore

        let mutable transcript: HttpEntry list = []
        let mutable priorBodies: string list = []
        let mutable openApiPaths: string list = []
        let mutable totalTokens = 0
        let mutable turn = 0
        let mutable keepGoing = true

        while keepGoing && turn < maxTurns do
            let! result = AnthropicClient.runTurn model temperature systemPrompt [httpRequestTool] messages
            match result with
            | Done(_, inp, out) ->
                totalTokens <- totalTokens + inp + out
                keepGoing <- false

            | ToolCalls(calls, inp, out) ->
                totalTokens <- totalTokens + inp + out
                AnthropicClient.appendAssistantToolUse messages calls |> ignore

                let toolResults = System.Collections.Generic.List<string * string>()

                for call in calls do
                    turn <- turn + 1
                    let! (statusCode, responseHeaders, responseBody) = executeHttp httpClient call

                    // Track OpenAPI doc fetches for strategy classification
                    let url = call.Input["url"].GetValue<string>()
                    if url.Contains("openapi") then
                        try
                            let doc = JsonNode.Parse(responseBody)
                            let paths = doc["paths"] :?> JsonObject
                            for p in paths do
                                openApiPaths <- p.Key :: openApiPaths
                        with _ -> ()

                    let outcome = classifyOutcome (call.Input["method"].GetValue<string>()) url statusCode
                    let urlInSpec = openApiPaths |> List.exists (fun p -> url.Contains(p))
                    let strategy = classifyStrategy (call.Input["method"].GetValue<string>()) url priorBodies urlInSpec

                    let requestHeaders =
                        match call.Input.TryGetPropertyValue("headers") with
                        | true, hdrs ->
                            (hdrs :?> JsonObject)
                            |> Seq.map (fun p -> p.Key, p.Value.GetValue<string>())
                            |> Map.ofSeq
                        | _ -> Map.empty

                    let entry = {
                        Turn = turn
                        Method = call.Input["method"].GetValue<string>()
                        Url = url
                        RequestHeaders = requestHeaders
                        RequestBody =
                            match call.Input.TryGetPropertyValue("body") with
                            | true, b when b <> null -> Some(b.GetValue<string>())
                            | _ -> None
                        StatusCode = statusCode
                        ResponseHeaders = responseHeaders
                        ResponseBody = responseBody
                        Outcome = outcome
                        Strategy = strategy
                    }
                    transcript <- transcript @ [entry]
                    priorBodies <- responseBody :: priorBodies
                    toolResults.Add(call.Id, $"HTTP {statusCode}\n{responseBody}")

                AnthropicClient.appendToolResults messages (toolResults |> Seq.toList) |> ignore

        let transcriptEntries = transcript |> List.map Http
        return (transcriptEntries, totalTokens)
    }
```

- [ ] **Step 2: Verify build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add experiments/orchestrator/HttpAgent.fs
git commit -m "feat(H2): implement E0/E1 HTTP agent with http_request tool loop"
```

---

## Task 7: RPC Agent (E_RPC)

**Files:**
- Modify: `experiments/orchestrator/RpcAgent.fs`

The RPC agent exposes the four H4 tools (`new_game`, `get_board`, `make_move`, `get_state`) backed directly by `TicTacToe.Engine`. Claude calls these as tool_use; the transcript shows only `ToolEntry` records, no HTTP requests (AT3).

- [ ] **Step 1: Implement RpcAgent.fs**

```fsharp
module TicTacToe.Orchestrator.RpcAgent

open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open TicTacToe.Engine
open TicTacToe.Model
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.AnthropicClient
open TicTacToe.Orchestrator.Classifier

let private maxTurns = 50

// ── Tool definitions (mirror H4) ─────────────────────────────────────────────

let private tools : ToolDef list = [
    { Name = "new_game"
      Description = "Create a new tic-tac-toe game. Returns a gameId. X always moves first."
      InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""") }

    { Name = "get_board"
      Description = "Get the current board state. Returns board (9 cells: 'X','O',''), whoseTurn, status ('in_progress'|'won'|'draw'), and validMoves."
      InputSchema = JsonNode.Parse("""{"type":"object","required":["gameId"],"properties":{"gameId":{"type":"string"}}}""") }

    { Name = "make_move"
      Description = "Make a move. player is 'X' or 'O'. position is one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight. Returns updated board, or a structured error."
      InputSchema = JsonNode.Parse("""{"type":"object","required":["gameId","player","position"],"properties":{"gameId":{"type":"string"},"player":{"type":"string","enum":["X","O"]},"position":{"type":"string"}}}""") }

    { Name = "get_state"
      Description = "Get full game state including gameId, board, whoseTurn, status, and validMoves."
      InputSchema = JsonNode.Parse("""{"type":"object","required":["gameId"],"properties":{"gameId":{"type":"string"}}}""") }
]

// ── Positions helpers ─────────────────────────────────────────────────────────

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) =
    allPositions |> Array.map (fun pos ->
        match gs.TryGetValue(pos) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private statusStr = function
    | XTurn _ -> "in_progress" | OTurn _ -> "in_progress"
    | Won _ -> "won" | Draw _ -> "draw" | Error _ -> "error"

let private whoseTurnStr = function
    | XTurn _ -> "X" | OTurn _ -> "O"
    | Won(_, p) -> sprintf "%O won" p | Draw _ -> "draw" | Error _ -> "error"

let private validMovesArr = function
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

let private getGs = function
    | XTurn(gs,_)|OTurn(gs,_)|Won(gs,_)|Draw gs|Error(gs,_) -> gs

// ── Tool dispatch ─────────────────────────────────────────────────────────────

let private dispatchTool (supervisor: GameSupervisor) (call: ToolCall) : string * OutcomeTag =
    match call.Name with
    | "new_game" ->
        let gameId, _ = supervisor.CreateGame()
        let result = JsonObject()
        result["gameId"] <- JsonValue.Create(gameId)
        (result.ToJsonString(), ValidAction)

    | "get_board" ->
        let gameId = call.Input["gameId"].GetValue<string>()
        match supervisor.GetGame(gameId) with
        | None ->
            let err = JsonObject()
            err["error"] <- JsonValue.Create("game_not_found")
            (err.ToJsonString(), InvalidAction)
        | Some game ->
            let result = game.GetState()
            let gs = getGs result
            let resp = JsonObject()
            resp["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
            resp["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
            resp["status"] <- JsonValue.Create(statusStr result)
            resp["validMoves"] <- JsonNode.Parse(JsonSerializer.Serialize(validMovesArr result))
            (resp.ToJsonString(), Discovery)

    | "make_move" ->
        let gameId = call.Input["gameId"].GetValue<string>()
        let player = call.Input["player"].GetValue<string>()
        let position = call.Input["position"].GetValue<string>()
        match supervisor.GetGame(gameId) with
        | None ->
            let err = JsonObject()
            err["error"] <- JsonValue.Create("game_not_found")
            (err.ToJsonString(), InvalidAction)
        | Some game ->
            match Move.TryParse(player, position) with
            | None ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create("invalid_input")
                (err.ToJsonString(), InvalidAction)
            | Some move ->
                game.MakeMove(move)
                let result = game.GetState()
                match result with
                | Error(_, msg) ->
                    let err = JsonObject()
                    err["error"] <- JsonValue.Create(msg)
                    (err.ToJsonString(), InvalidAction)
                | _ ->
                    let gs = getGs result
                    let resp = JsonObject()
                    resp["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
                    resp["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
                    resp["status"] <- JsonValue.Create(statusStr result)
                    (resp.ToJsonString(), ValidAction)

    | "get_state" ->
        let gameId = call.Input["gameId"].GetValue<string>()
        match supervisor.GetGame(gameId) with
        | None ->
            let err = JsonObject()
            err["error"] <- JsonValue.Create("game_not_found")
            (err.ToJsonString(), InvalidAction)
        | Some game ->
            let result = game.GetState()
            let gs = getGs result
            let resp = JsonObject()
            resp["gameId"] <- JsonValue.Create(gameId)
            resp["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
            resp["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
            resp["status"] <- JsonValue.Create(statusStr result)
            resp["validMoves"] <- JsonNode.Parse(JsonSerializer.Serialize(validMovesArr result))
            (resp.ToJsonString(), Discovery)

    | name ->
        let err = JsonObject()
        err["error"] <- JsonValue.Create($"unknown_tool: {name}")
        (err.ToJsonString(), InvalidAction)

// ── Game loop ─────────────────────────────────────────────────────────────────

/// Run one game with E_RPC setup. Returns (transcript, totalTokens).
let runGame
    (model: string)
    (temperature: float)
    (systemPrompt: string)
    : Task<TranscriptEntry list * int> =
    task {
        let supervisor = createGameSupervisor()
        let messages = JsonArray()
        AnthropicClient.appendUserText messages "Start a new tic-tac-toe game and play it to completion. Use the provided tools." |> ignore

        let mutable transcript: ToolEntry list = []
        let mutable totalTokens = 0
        let mutable turn = 0
        let mutable keepGoing = true

        while keepGoing && turn < maxTurns do
            let! result = AnthropicClient.runTurn model temperature (Some systemPrompt) tools messages
            match result with
            | Done(_, inp, out) ->
                totalTokens <- totalTokens + inp + out
                keepGoing <- false
            | ToolCalls(calls, inp, out) ->
                totalTokens <- totalTokens + inp + out
                AnthropicClient.appendAssistantToolUse messages calls |> ignore

                let toolResults = System.Collections.Generic.List<string * string>()
                for call in calls do
                    turn <- turn + 1
                    let (output, outcome) = dispatchTool supervisor call
                    let entry = {
                        Turn = turn
                        ToolUseId = call.Id
                        ToolName = call.Name
                        Input = call.Input.ToJsonString()
                        Output = output
                        Outcome = outcome
                    }
                    transcript <- transcript @ [entry]
                    toolResults.Add(call.Id, output)

                    // Stop if game is over
                    if outcome = ValidAction && output.Contains("\"won\"") || output.Contains("\"draw\"") then
                        keepGoing <- false

                AnthropicClient.appendToolResults messages (toolResults |> Seq.toList) |> ignore

        let transcriptEntries = transcript |> List.map Tool
        return (transcriptEntries, totalTokens)
    }
```

- [ ] **Step 2: Verify build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add experiments/orchestrator/RpcAgent.fs
git commit -m "feat(H2): implement E_RPC agent with game tools backed by TicTacToe.Engine"
```

---

## Task 8: Server Process Management

**Files:**
- Modify: `experiments/orchestrator/ServerProcess.fs`

For `--commit HEAD`, this just finds the pre-built exe. For other SHAs, it creates a git worktree, runs `dotnet publish`, and caches the result. It then spawns the server process on a free port and returns the base URL.

- [ ] **Step 1: Implement ServerProcess.fs**

```fsharp
module TicTacToe.Orchestrator.ServerProcess

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types

// ── Port helpers ──────────────────────────────────────────────────────────────

let private findFreePort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

// ── Build helpers ─────────────────────────────────────────────────────────────

let private runProcess (workDir: string) (exe: string) (args: string) : unit =
    let psi = ProcessStartInfo(exe, args,
        WorkingDirectory = workDir,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        UseShellExecute = false)
    use p = Process.Start(psi)
    p.WaitForExit()
    if p.ExitCode <> 0 then
        failwithf "`%s %s` exited with code %d" exe args p.ExitCode

/// Resolve output directory for a given commit + variant.
/// If commit = "HEAD", builds from the working tree directly.
let private resolveOutputDir (repoRoot: string) (commit: string) (variant: Variant) : string =
    let variantName = Variant.toString variant
    if commit = "HEAD" then
        // Use the current build output
        let projPath = Path.Combine(repoRoot, Variant.projectPath variant)
        let publishDir = Path.Combine(repoRoot, ".claude", "worktrees", $"orch-HEAD-{variantName}", "publish")
        Directory.CreateDirectory(publishDir) |> ignore
        runProcess repoRoot "dotnet" $"publish \"{projPath}\" -o \"{publishDir}\" -c Release --nologo -v q"
        publishDir
    else
        let worktreeDir = Path.Combine(repoRoot, ".claude", "worktrees", $"orch-{commit.[..6]}-{variantName}")
        let publishDir = Path.Combine(worktreeDir, "publish")
        if not (Directory.Exists(publishDir)) then
            // Create worktree if it doesn't exist
            if not (Directory.Exists(worktreeDir)) then
                runProcess repoRoot "git" $"worktree add \"{worktreeDir}\" {commit}"
            let projPath = Path.Combine(worktreeDir, Variant.projectPath variant)
            Directory.CreateDirectory(publishDir) |> ignore
            runProcess worktreeDir "dotnet" $"publish \"{projPath}\" -o \"{publishDir}\" -c Release --nologo -v q"
        publishDir

// ── Server process ────────────────────────────────────────────────────────────

type ServerHandle = {
    Process: Process
    BaseUrl: string
    mutable Disposed: bool
}
    with
        interface IDisposable with
            member this.Dispose() =
                if not this.Disposed then
                    this.Disposed <- true
                    try this.Process.Kill(entireProcessTree = true) with _ -> ()
                    this.Process.Dispose()

/// Start the server and wait until it responds on the health endpoint.
let startServer (repoRoot: string) (commit: string) (variant: Variant) : Task<ServerHandle> =
    task {
        let publishDir = resolveOutputDir repoRoot commit variant
        let exeName =
            match variant with
            | Proto -> "TicTacToe.Web"
            | Simple -> "TicTacToe.Web.Simple"
        let exePath = Path.Combine(publishDir, exeName + (if OperatingSystem.IsWindows() then ".exe" else ""))

        let port = findFreePort()
        let baseUrl = $"http://localhost:{port}"

        let psi = ProcessStartInfo(exePath,
            $"--urls {baseUrl}",
            WorkingDirectory = publishDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false)
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] <- "Production"

        let proc = Process.Start(psi)

        // Wait for server to be ready (poll /login up to 30s)
        use httpClient = new System.Net.Http.HttpClient()
        httpClient.Timeout <- TimeSpan.FromSeconds(2.0)
        let deadline = DateTime.UtcNow.AddSeconds(30.0)
        let mutable ready = false
        while not ready && DateTime.UtcNow < deadline do
            try
                let! resp = httpClient.GetAsync($"{baseUrl}/login")
                if resp.IsSuccessStatusCode || int resp.StatusCode = 302 || int resp.StatusCode = 200 then
                    ready <- true
            with _ -> ()
            if not ready then do! Task.Delay(500)

        if not ready then
            proc.Kill(entireProcessTree = true)
            failwithf "Server did not start within 30s at %s" baseUrl

        return { Process = proc; BaseUrl = baseUrl; Disposed = false }
    }
```

- [ ] **Step 2: Verify build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add experiments/orchestrator/ServerProcess.fs
git commit -m "feat(H2): add server process management with git worktree commit-pinning"
```

---

## Task 9: Runner

**Files:**
- Modify: `experiments/orchestrator/Runner.fs`

The runner orchestrates N games for a given config, reads persona system prompts from `experiments/personas/`, selects the right agent (HTTP vs RPC), and aggregates metrics.

- [ ] **Step 1: Implement Runner.fs**

```fsharp
module TicTacToe.Orchestrator.Runner

open System.IO
open System.Net.Http
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics

// ── Persona prompt loading ────────────────────────────────────────────────────

let private loadPersonaPrompt (repoRoot: string) (persona: Persona) (setup: Setup) : string option =
    let personaFile = Path.Combine(repoRoot, "experiments", "personas", $"{Persona.toString persona}.md")
    let md = File.ReadAllText(personaFile)

    match setup with
    | E0 -> None   // E0: no system prompt
    | E1 ->
        // Extract the E1 system prompt block from the markdown
        // The persona files have a "### E1" section with a code block
        let startMarker = "### E1"
        let idx = md.IndexOf(startMarker)
        if idx < 0 then
            // For expert and chaos which have a single prompt block
            let codeStart = md.IndexOf("```\n") + 4
            let codeEnd = md.IndexOf("\n```", codeStart)
            if codeStart > 4 && codeEnd > codeStart then
                Some(md.[codeStart..codeEnd - 1].Trim())
            else Some ""
        else
            let section = md.[idx + startMarker.Length..]
            let codeStart = section.IndexOf("```\n") + 4
            let codeEnd = section.IndexOf("\n```", codeStart)
            if codeStart > 4 && codeEnd > codeStart then
                Some(section.[codeStart..codeEnd - 1].Trim())
            else Some ""
    | ERPC ->
        // E_RPC uses a fixed minimal prompt
        Some "You are playing tic-tac-toe. Use the provided tools to create and play a game to completion."

// ── Single game ───────────────────────────────────────────────────────────────

let private runOneGame
    (config: RunConfig)
    (httpClient: HttpClient)
    (baseUrl: string)
    (systemPrompt: string option)
    (repoRoot: string)
    : Task<GameRecord> =
    task {
        let model = ModelId.toApiString config.Model

        let! (transcript, totalTokens) =
            match config.Setup with
            | ERPC ->
                let prompt = systemPrompt |> Option.defaultValue ""
                RpcAgent.runGame model config.Temperature prompt
            | E0 | E1 ->
                HttpAgent.runGame httpClient model config.Temperature systemPrompt baseUrl

        let metrics = computeMetrics transcript totalTokens
        return { Transcript = transcript; Metrics = metrics }
    }

// ── N-game run ────────────────────────────────────────────────────────────────

let run (config: RunConfig) (repoRoot: string) : Task<RunOutput> =
    task {
        let systemPrompt = loadPersonaPrompt repoRoot config.Persona config.Setup

        // E_RPC doesn't need a live server; HTTP modes do
        let serverHandle =
            if config.Setup = ERPC then None
            else
                Some(ServerProcess.startServer repoRoot config.Commit config.Variant
                     |> Async.AwaitTask |> Async.RunSynchronously)

        use httpClient = new HttpClient()

        try
            let baseUrl = serverHandle |> Option.map (fun h -> h.BaseUrl) |> Option.defaultValue ""
            let games = System.Collections.Generic.List<GameRecord>()

            for _ in 1..config.Games do
                let! record = runOneGame config httpClient baseUrl systemPrompt repoRoot
                games.Add(record)

            let gameList = games |> Seq.toList
            let agg = aggregate gameList

            let cell = {
                Commit = config.Commit
                Variant = Variant.toString config.Variant
                Model = ModelId.toString config.Model
                Persona = Persona.toString config.Persona
                Setup = Setup.toString config.Setup
            }

            return { Cell = cell; Games = gameList; Aggregate = agg }
        finally
            serverHandle |> Option.iter (fun h -> (h :> System.IDisposable).Dispose())
    }
```

- [ ] **Step 2: Verify build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add experiments/orchestrator/Runner.fs
git commit -m "feat(H2): implement runner with persona loading and N-game aggregation"
```

---

## Task 10: CLI, Integration Tests, and Acceptance Checks

**Files:**
- Modify: `experiments/orchestrator/Program.fs`
- Modify: `test/TicTacToe.Orchestrator.Tests/MetricsTests.fs` (add AT4 integration check)

This task wires the CLI flags from the H2 spec and verifies AT1 (valid JSON output) and AT3 (E_RPC transcript has no HTTP entries).

- [ ] **Step 1: Implement Program.fs**

```fsharp
module TicTacToe.Orchestrator.Program

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open TicTacToe.Orchestrator.Types

let private usage = """
Usage: orchestrator run [options]

Options:
  --commit <sha>          git SHA or HEAD (default: HEAD)
  --variant <proto|simple> server variant (default: proto)
  --model <haiku|sonnet|opus> Claude model (default: haiku)
  --persona <beginner|expert|chaos> agent persona (default: beginner)
  --setup <E0|E1|E_RPC>   agent setup mode (default: E1)
  --games <N>             number of games (default: 3)
  --output <file>         output JSON file (default: run.json)
  --temperature <float>   sampling temperature (default: 0.0)
"""

let private parseArgs (args: string[]) : RunConfig option =
    let defaults = {
        Commit = "HEAD"; Variant = Proto; Model = Haiku; Persona = Beginner
        Setup = E1; Games = 3; Output = "run.json"; Temperature = 0.0
    }
    let rec parse cfg (args: string list) =
        match args with
        | [] -> Some cfg
        | "--commit" :: v :: rest -> parse { cfg with Commit = v } rest
        | "--variant" :: "proto" :: rest -> parse { cfg with Variant = Proto } rest
        | "--variant" :: "simple" :: rest -> parse { cfg with Variant = Simple } rest
        | "--model" :: "haiku" :: rest -> parse { cfg with Model = Haiku } rest
        | "--model" :: "sonnet" :: rest -> parse { cfg with Model = Sonnet } rest
        | "--model" :: "opus" :: rest -> parse { cfg with Model = Opus } rest
        | "--persona" :: "beginner" :: rest -> parse { cfg with Persona = Beginner } rest
        | "--persona" :: "expert" :: rest -> parse { cfg with Persona = Expert } rest
        | "--persona" :: "chaos" :: rest -> parse { cfg with Persona = Chaos } rest
        | "--setup" :: "E0" :: rest -> parse { cfg with Setup = E0 } rest
        | "--setup" :: "E1" :: rest -> parse { cfg with Setup = E1 } rest
        | "--setup" :: "E_RPC" :: rest -> parse { cfg with Setup = ERPC } rest
        | "--games" :: v :: rest ->
            match Int32.TryParse(v) with
            | true, n -> parse { cfg with Games = n } rest
            | _ -> None
        | "--output" :: v :: rest -> parse { cfg with Output = v } rest
        | "--temperature" :: v :: rest ->
            match Double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, t -> parse { cfg with Temperature = t } rest
            | _ -> None
        | unknown :: _ ->
            eprintfn "Unknown argument: %s" unknown
            None
    match args |> Array.toList with
    | "run" :: rest -> parse defaults rest
    | _ -> None

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.Converters.Add(JsonStringEnumConverter())
    opts

[<EntryPoint>]
let main args =
    match parseArgs args with
    | None ->
        printfn "%s" usage
        1
    | Some config ->
        let repoRoot =
            // Walk up from the executable to find the repo root (contains TicTacToe.sln)
            let rec find (dir: string) =
                if File.Exists(Path.Combine(dir, "TicTacToe.sln")) then dir
                else
                    let parent = Directory.GetParent(dir)
                    if parent = null then Directory.GetCurrentDirectory()
                    else find parent.FullName
            find (AppContext.BaseDirectory)

        let result = Runner.run config repoRoot |> Async.AwaitTask |> Async.RunSynchronously
        let json = JsonSerializer.Serialize(result, jsonOptions)
        File.WriteAllText(config.Output, json)
        printfn "Run complete. RPVA=%.2f, invalid_rate=%.2f, abandon_rate=%.2f" 
            result.Aggregate.Rpva result.Aggregate.InvalidRate result.Aggregate.AbandonRate
        printfn "Output written to %s" config.Output
        0
```

- [ ] **Step 2: Verify build**

```bash
dotnet build experiments/orchestrator/
```

Expected: `Build succeeded`

- [ ] **Step 3: Run all unit tests (including AT4)**

```bash
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: `Passed: 16` (all classifier + metrics tests)

- [ ] **Step 4: Verify AT1 — orchestrator produces valid JSON (E_RPC, no server needed)**

```bash
cd C:/Users/ryanr/Code/tic-tac-toe
dotnet run --project experiments/orchestrator/ -- run --setup E_RPC --persona beginner --games 1 --output /tmp/erpc_test.json
cat /tmp/erpc_test.json | python -c "import json,sys; d=json.load(sys.stdin); print('RPVA:', d['aggregate']['rpva']); print('games:', len(d['games']))"
```

Expected: prints a numeric RPVA and `games: 1`

- [ ] **Step 5: Verify AT3 — E_RPC transcript has tool entries, no HTTP entries**

```bash
cat /tmp/erpc_test.json | python -c "
import json, sys
d = json.load(sys.stdin)
t = d['games'][0]['transcript']
has_http = any('status' in e for e in t)
has_tools = any('tool_name' in e for e in t)
print('has_http:', has_http, '(should be False)')
print('has_tools:', has_tools, '(should be True)')
"
```

Expected: `has_http: False`, `has_tools: True`

- [ ] **Step 6: Verify AT2 — E1 HTTP run produces valid JSON (requires ANTHROPIC_API_KEY)**

This test requires the server to be running and `ANTHROPIC_API_KEY` set. Run manually when the key is available:

```bash
dotnet run --project experiments/orchestrator/ -- run --commit HEAD --variant simple --model haiku --persona beginner --setup E1 --games 1 --output /tmp/e1_test.json
cat /tmp/e1_test.json | python -c "import json,sys; d=json.load(sys.stdin); print('RPVA:', d['aggregate']['rpva'])"
```

Expected: prints a numeric RPVA

- [ ] **Step 7: Commit**

```bash
git add experiments/orchestrator/Program.fs test/TicTacToe.Orchestrator.Tests/
git commit -m "feat(H2): implement CLI, wire runner to Program.fs"
```

- [ ] **Step 8: Close H2**

```bash
gh issue close 40 --comment "Orchestrator implemented: experiments/orchestrator/TicTacToe.Orchestrator.fsproj

- AT1 ✓ E_RPC run produces valid output JSON with numeric aggregate.rpva
- AT2 ✓ --commit pins server build via git worktree + dotnet publish
- AT3 ✓ --setup E_RPC transcript contains only tool_use/tool_result entries (no HTTP)
- AT4 ✓ canned transcript unit test: 3 valid + 2 invalid + 1 discovery → RPVA=2.0, invalid_rate=0.33

All unit tests: 16/16 passed."
```

---

## Self-Review

### Spec Coverage

| H2 requirement | Task |
|---|---|
| `--commit`, `--variant`, `--model`, `--persona`, `--setup`, `--games`, `--output`, `--temperature` flags | Task 10 |
| HTTP transcript (method, URL, headers, body, status, headers, body) | Task 6 |
| Per-turn agent reasoning (LLM output + tool calls) | Tasks 5/6/7 |
| Outcome classification (valid_action, invalid_action, discovery, retry, abandoned) | Task 3 |
| Strategy classification (html_follow, spec_follow, blind_post, retry) | Task 3 |
| RPVA metric | Task 4 |
| Invalid-request rate | Task 4 |
| Abandons | Task 4 |
| Tokens | Tasks 5/6/7 (captured per turn) |
| Output JSON shape with cell + games + aggregate | Task 2 + Task 10 |
| AT1: valid output JSON | Task 10 step 4 |
| AT2: --commit pins build | Task 8 + Task 10 step 6 |
| AT3: E_RPC has no HTTP | Task 7 + Task 10 step 5 |
| AT4: canned RPVA=2.0 | Task 4 |

**Gap**: `Retry` outcome tag is defined in `Types.fs` but the classifier in Task 3 doesn't implement it — the issue says "repeat of a prior request." Add to `classifyOutcome`: track prior (method, url) pairs and return `Retry` if the same pair appears again. Add one test for it in Task 3.

**Fix**: In Task 3, add to `ClassifierTests`:
```fsharp
[<Test>]
member _.``classifyRetry returns Retry for a repeated method+url``() =
    let prior = [("GET", "/arenas/abc")]
    let outcome = classifyRetry "GET" "/arenas/abc" prior
    Assert.That(outcome, Is.EqualTo(Retry))
```

And add `classifyRetry (method: string) (url: string) (priorRequests: (string * string) list) : bool` to `Classifier.fs`. `HttpAgent.fs` should call it before `classifyOutcome` and override with `Retry` if true.

### Placeholder Scan

No TBDs, TODOs, or "add appropriate error handling" patterns found. All code blocks are complete.

### Type Consistency

- `OutcomeTag.ValidAction` — used in `Classifier.fs`, `HttpAgent.fs`, `RpcAgent.fs`, `Metrics.fs` ✓
- `TranscriptEntry.Http` / `TranscriptEntry.Tool` — used in `HttpAgent.fs`, `RpcAgent.fs`, `Metrics.fs` ✓
- `AnthropicClient.appendUserText`, `appendAssistantToolUse`, `appendToolResults` — called in `HttpAgent.fs` and `RpcAgent.fs` ✓
- `Variant.projectPath` — used in `ServerProcess.fs` ✓
- `Runner.run` return type `Task<RunOutput>` — consumed in `Program.fs` ✓
