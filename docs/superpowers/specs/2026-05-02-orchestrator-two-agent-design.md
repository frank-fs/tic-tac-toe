# Orchestrator Redesign: Multi-Agent Execution

**Issue:** TBD (G0 #41 reopened pending this redesign)
**Date:** 2026-05-02
**Status:** Design draft — pending user review
**Supersedes:** Single-agent execution model in current `experiments/orchestrator/`

---

## Purpose

Rebuild the orchestrator to execute the agent-hypothesis experiment correctly. The current single-agent design is fundamentally wrong for tic-tac-toe — the game is two-player, the server enforces player identity, and the experiment requires measuring how independent agents discover roles, respect turn boundaries, and behave under adversarial conditions.

Goals:

1. Three concurrent autonomous agents per game (X, O, Observer) — roles emerge from server enforcement, not pre-assignment
2. Multi-tool agents using off-the-shelf MCP servers (no custom tools written for this experiment)
3. Three-layer instrumentation: LLM message history, tool execution, structured server logs
4. F# end-to-end — orchestrator and agent runners share a single process
5. Cell matrix as F# data, no shell scripts
6. Per-cell JSON persistence on disk; SQLite analysis layer optional later

Non-goals for this redesign:

- Production deployment of the orchestrator (research instrument, runs on the user's hardware)
- Custom interception layer between agents and tools (defer; add only if needed)
- Multi-game environments per cell (defer; lock to one game per cell)
- Path parity between server variants and HATEOAS fallback for V_proto (defer)
- Reproducibility of agent behavior across runs (agents are non-deterministic; not pursued)

---

## Background — what is wrong with the current design

| Issue | Current behavior | Why it is wrong |
|-------|-----------------|-----------------|
| Single agent plays both X and O | One LLM session, alternates `make_move` for X and O | The game is two-player. Server enforces one identity per role. |
| `make_move` accepts arbitrary `player` arg | Tool signature: `(player: "X" \| "O", position)` | An agent locked to role X should not be able to invoke moves for O. |
| HTTP cells abandon | Server rejects single agent attempting both roles → agent stuck | This is correct server behavior being misinterpreted as a harness bug. |
| E_RPC succeeds via self-play | No auth or player enforcement in `RpcAgent` tools | This is actually a finding — it is the broken-server baseline. Keep behavior, reframe interpretation. |
| Custom `http_request` tool with no cookie persistence | Each call is a fresh request | Real agents need session continuity, especially for cookie auth on V_proto. |
| Tool execution fully proxied through orchestrator | Orchestrator wraps every HTTP request and tool dispatch | Locks the experiment to a custom HTTP tool. Browser/Playwright tools cannot fit this model cleanly. |

The current orchestrator was an LLM proxy. The redesign makes it a research instrument with privileged observation, where agents are autonomous.

---

## Architecture

### Process model — one F# process

```
TicTacToe.Orchestrator (F# console app)
├── Orchestrator MailboxProcessor  (top-level supervisor)
│   ├── for each cell in matrix:
│   │   ├── start app server (env vars from CellSpec)
│   │   ├── start structured server-log tail
│   │   ├── createAgent X, O, Observer (3 MailboxProcessors)
│   │   ├── monitor for game completion
│   │   ├── send Stop to all agents, collect transcripts
│   │   ├── persist cell result (transcripts + server-logs + metrics)
│   │   └── teardown agents + server
│   └── write matrix manifest, exit
├── Agent MailboxProcessor (3 instances per cell)
│   ├── connects to its own MCP servers (own browser context, own cookie jar)
│   ├── runs LLM turn loop: API call → tool execution via MCP → repeat
│   └── replies to GetTranscript with full message history on Stop
├── McpClient module
│   ├── wraps ModelContextProtocol .NET SDK
│   └── manages per-agent MCP server connections
├── AnthropicClient module (existing, lightly modified)
│   └── direct calls to LM Studio's Anthropic-compatible /v1/messages
├── ServerProcess module (existing, modified)
│   └── starts app servers with env-var configuration
└── ServerLogTail module (new)
    └── reads server's structured JSONL logs and exposes filtered events
```

No subprocess for agents. No PowerShell scripts. No external runtime dependencies beyond .NET 10 SDK, the configured MCP servers (each launched as needed), and a running LM Studio.

### Concurrency primitives

- **Orchestrator** is a `MailboxProcessor<OrchestratorMsg>` — coordinates matrix iteration, owns server lifecycle, owns agent lifecycle
- **Each agent** is a `MailboxProcessor<AgentMsg>` — owns one LLM conversation, one MCP client connection set, one transcript
- **MailboxProcessor** mirrors the actor pattern in `TicTacToe.Web/Handlers.fs` (game subscriptions, broadcast channels) — consistent with the rest of the codebase

### Message protocols

```fsharp
type OrchestratorMsg =
    | StartMatrix of CellSpec list * AsyncReplyChannel<unit>
    | CellComplete of CellId * CellResult
    | Shutdown of AsyncReplyChannel<unit>

type AgentMsg =
    | Start
    | Stop of AsyncReplyChannel<AgentTranscript>
    | GetState of AsyncReplyChannel<AgentSnapshot>
```

`Start` kicks the agent's turn loop. The agent posts itself a `Tick` (or recurses) after each turn until `Stop` arrives or max turns is reached. `Stop` halts the loop and returns the full transcript.

---

## Agent design

### Construction

```fsharp
type AgentRole = X | O | Observer  // declared by orchestrator, NOT told to agent

type AgentConfig = {
    Id: string                         // "x" | "o" | "observer" — for orchestrator bookkeeping only
    Persona: Persona                   // beginner | expert | chaos
    Model: string                      // LM Studio model id, passed verbatim
    BaseUrl: string                    // server URL the agent should explore
    McpServers: McpServerConfig list   // playwright, fetch, etc.
    MaxTurns: int                      // safety limit; default 50
    Temperature: float                 // default 0.0
}

let createAgent (config: AgentConfig) : MailboxProcessor<AgentMsg>
```

### What the agent knows at start

- The server URL
- Its persona system prompt
- The list of MCP servers (and therefore which tools it has access to)
- A first user message: minimal, e.g., `"Here is a URL: {baseUrl}"` — no role injection

### What the agent does NOT know

- Which role it will be assigned (X, O, or rejected as third)
- Whether other agents are present
- That it might end up as an "observer"

The agent discovers all of this through interaction with the server and its tools. The persona shapes how it reacts to discoveries (chaos persona may probe rejection responses for weaknesses; beginner persona accepts a 4xx and waits or quits).

### Persona behavior framing

| Persona | When given a URL with available tools | When server rejects a move |
|---------|---------------------------------------|---------------------------|
| Beginner | Visits, follows obvious affordances, plays valid moves | Backs off, polls or waits |
| Expert | Optimal play, recognizes affordances quickly, manages session deliberately | Recognizes 4xx as turn enforcement, waits efficiently |
| Chaos | Probes for weaknesses: invalid moves, out-of-turn moves, malformed inputs, attempts to play as both X and O | Continues probing after rejection, looks for ways around enforcement |

Personas are stored as F# data (system prompt + behavior tags), not parsed from markdown files. This eliminates the brittle markdown extraction in the current `loadPersonaPrompt`.

---

## Cell matrix

### CellSpec type

```fsharp
type Variant = Proto | Simple | ERPC

type CellSpec = {
    Id: string                                              // unique cell identifier, e.g. "smoke-proto-bbb"
    Variant: Variant
    Personas: Persona * Persona * Persona                   // (X-launch, O-launch, Observer-launch); roles emergent
    Model: string                                           // LM Studio model id
    InitialGames: int                                       // server env: TICTACTOE_INITIAL_GAMES
    MaxGames: int                                           // server env: TICTACTOE_MAX_GAMES
    MaxTurnsPerAgent: int                                   // safety
    McpServers: McpServerConfig list                        // tool set for ALL three agents
    Temperature: float
}
```

### Smoke matrix

Defined in `experiments/orchestrator/Matrices/Smoke.fs`. Six cells, all with `(Beginner, Beginner, Beginner)` to validate the harness without persona variability:

```fsharp
let smoke : CellSpec list = [
    cell "smoke-proto-bbb"  Proto Beginner Beginner Beginner
    cell "smoke-simple-bbb" Simple Beginner Beginner Beginner
    cell "smoke-erpc-bbb"   ERPC Beginner Beginner Beginner
    cell "smoke-erpc-bbc"   ERPC Beginner Beginner Chaos       // chaos in ERPC: expected to wreak havoc
    cell "smoke-proto-bbc"  Proto Beginner Beginner Chaos      // chaos third agent: tests observer rejection
    cell "smoke-simple-bbc" Simple Beginner Beginner Chaos
]
```

The smoke matrix validates:
- All three variants run end-to-end without orchestrator errors
- Two beginners can complete a game cooperatively (Proto + Simple)
- E_RPC permits self-play (broken-server baseline)
- E_RPC chaos finding visible (no enforcement → free reign)
- Server enforcement rejects the third party (Proto + Simple)

### Future matrices

`experiments/orchestrator/Matrices/F0.fs`, `F1.fs`, etc. — the F-axis matrix. Defined per F-issue when each is scoped.

### Invocation

```bash
dotnet run --project experiments/orchestrator/ -- smoke
dotnet run --project experiments/orchestrator/ -- f0
```

The `Program.fs` `main` selects a matrix by name and hands it to the Orchestrator MailboxProcessor.

---

## Tool access via MCP

### MCP client integration

The orchestrator embeds the `ModelContextProtocol` .NET SDK as the MCP client. Each agent has its own client instance — separate processes for the MCP servers spawned per agent (so cookie state, browser contexts, etc. do not leak between X, O, and Observer).

### Default MCP servers per cell

Smoke matrix defaults:

| MCP server | Purpose | Used in |
|-----------|---------|---------|
| `playwright` (npm: @playwright/mcp) | Browser automation: navigate, click, fill, evaluate, screenshot, cookies | Proto, Simple |
| `fetch` (npm: @modelcontextprotocol/server-fetch) | HTTP GET/POST with optional cookie jar | Proto fallback, Simple |
| (TBD: ERPC tool source) | new_game, get_board, make_move, get_state | ERPC only |

### Open design decision: how E_RPC tools are exposed

E_RPC's engine-backed tools (`new_game`, `get_board`, `make_move`, `get_state`) do not exist as an off-the-shelf MCP server. Three options:

- **A** — Build a small `TicTacToe.Mcp` project that wraps the engine and exposes these as MCP tools. Architecture stays uniform; agents always reach tools via MCP. Adds a small new project.
- **B** — Keep the current `RpcAgent.fs` in-process pattern: orchestrator hosts the engine and exposes RPC tools directly to the agent's API request. Inconsistent with "tools always via MCP" but smaller scope.
- **C** — Drop E_RPC as a separate variant. All cells use one of the HTTP variants; "no protocol" baseline disappears.

**Recommendation: A.** Uniform architecture pays off as the matrix grows. The `TicTacToe.Mcp` project is small (engine is already a NuGet-packageable library) and the F# MCP server SDK supports this pattern.

### Tool selection is itself a finding

The agent picks its own tool from whatever MCP servers expose. Examples of measurable tool-selection behavior:

- Does an agent on V_proto realize `fetch` is insufficient (no SSE, no JS) and switch to `playwright`?
- Does a chaos agent escalate to `playwright.evaluate` to inject JavaScript?
- Does the observer agent settle for screenshots when its move attempts are rejected?

---

## Three-layer instrumentation

### Layer 1 — LLM message history

Captured by the agent runner directly. After every API call, append the response (with `tool_use` blocks) and the tool results (synthesized from MCP execution) to a structured transcript. On `Stop`, the agent replies with:

```fsharp
type AgentTranscript = {
    AgentId: string
    Persona: Persona
    Messages: JsonArray              // raw Anthropic-format message history
    LlmTurns: LlmTurn list           // structured per-turn record
    Aborted: bool                    // true if max turns hit
}

type LlmTurn = {
    TurnIndex: int
    StopReason: string               // "tool_use" | "end_turn" | "max_tokens"
    InputTokens: int
    OutputTokens: int
    ToolCalls: ToolCallRecord list
    TextOutput: string option
    Timestamp: DateTimeOffset
}
```

### Layer 2 — tool execution

Wrapped by the McpClient. Every `mcp.callTool` invocation produces:

```fsharp
type ToolCallRecord = {
    ToolName: string                 // e.g., "playwright/navigate"
    Input: JsonNode
    Output: JsonNode option
    Error: string option
    DurationMs: int
    Timestamp: DateTimeOffset
}
```

Layer 2 records are embedded in the LlmTurn (via `ToolCalls`). They are also visible in Layer 1 (Anthropic message history includes tool_use + tool_result blocks), but with timing and error detail Layer 1 lacks.

### Layer 3 — structured server logs

Each app server (V_proto, V_simple, TicTacToe.Mcp) writes structured JSONL request logs to a configured path. Required fields per log entry:

```json
{
  "request_id": "uuid",
  "timestamp": "2026-05-02T14:30:00.123Z",
  "session_id": "cookie-derived",
  "game_id": "...",
  "player_role": "X | O | unassigned",
  "method": "POST",
  "path": "/games/{id}",
  "status_code": 202,
  "rejection_reason": null,
  "board_state_before": [...],
  "board_state_after": [...]
}
```

Plus game-lifecycle events on a separate stream (or filtered by `event_type`):

```json
{ "event_type": "game_created", "game_id": "...", "timestamp": "..." }
{ "event_type": "player_assigned", "game_id": "...", "session_id": "...", "role": "X" }
{ "event_type": "move_accepted", "game_id": "...", "session_id": "...", "move": "...", "board_state": [...] }
{ "event_type": "game_over", "game_id": "...", "outcome": "X_wins|O_wins|draw", "move_count": N }
```

The orchestrator tails these logs in real time during a cell run and writes them to `server-logs.jsonl` filtered to the cell's game.

### Correlation

All three layers are joined at analysis time:

- `session_id` (from server cookie) maps to AgentId — orchestrator records this mapping when the agent first authenticates
- `game_id` is constant per cell
- `timestamp` enables ordering across layers

No header injection or run IDs needed — the orchestrator's privileged view of agent sessions provides correlation by construction.

### Game completion signal

The orchestrator detects game over via combination signal:

1. **Server log `game_over` event** — primary, ground truth
2. **Agent observation in LLM transcript** — secondary, indicates whether the agent recognized the end state
3. **Max turns abandonment** — fallback, both agents hit their turn limit without server confirmation

After ground truth fires, the orchestrator gives agents a brief grace window (e.g., 5 seconds) to allow them to wrap up naturally, then sends `Stop`. Post-game-over actions during the grace window are recorded and tagged.

---

## Server-side changes

Both V_proto and V_simple need:

### Env-var configuration

| Env var | Default | Effect |
|---------|---------|--------|
| `TICTACTOE_INITIAL_GAMES` | 6 (V_proto current behavior) | Number of games to create on startup |
| `TICTACTOE_MAX_GAMES` | unlimited | Hard limit on total games |
| `TICTACTOE_REQUEST_LOG_PATH` | none (no logging) | Path to JSONL log file for structured request logs |

### Game-creation lockdown when at limit

When `MaxGames` is reached:

- HTML/Datastar templates: do not render the "create game" button or list affordance
- JSON responses (V_simple): omit the `create_game` link/affordance
- `POST /games` endpoint: returns `409 Conflict`
- E_RPC (TicTacToe.Mcp if pursued): `new_game` tool not in the tool list

### Structured request logging

Both servers write JSONL to `TICTACTOE_REQUEST_LOG_PATH` if set. One file per server process, the orchestrator filters by `game_id` post-hoc.

This is independent of the orchestrator — a real autonomous agent finding the game in the future could be observed via the same logs.

---

## Persistence layout

Per cell:

```
experiments/results/<matrix>/<cell-id>/
  ├── transcripts/
  │   ├── x.json            ← AgentTranscript for the agent that became X
  │   ├── o.json            ← AgentTranscript for the agent that became O
  │   └── observer.json     ← AgentTranscript for the agent that was rejected
  ├── server-logs.jsonl     ← Layer 3 logs filtered to this cell's game
  ├── metrics.json          ← computed RPVA per agent, role assignments, completion outcome
  └── cell-spec.json        ← snapshot of the CellSpec that produced this run
experiments/results/<matrix>/manifest.json   ← matrix def + per-cell summary
```

Per-agent files are named by *resolved* role (x/o/observer), not by launch order. The orchestrator tracks the mapping `agent-id (launch slot)` → `resolved-role` from the server log `player_assigned` events. Observer is whichever agent never got assigned a role.

`metrics.json` shape:

```json
{
  "cell_id": "smoke-proto-bbb",
  "outcome": "x_wins" | "o_wins" | "draw" | "abandoned",
  "completion_signal": "server_log" | "abandoned" | "agent_observed",
  "duration_seconds": 42.3,
  "role_assignment": {
    "agent-launch-1": "X",
    "agent-launch-2": "O",
    "agent-launch-3": "Observer"
  },
  "per_agent": {
    "X":        { "rpva": 1.2, "invalid_rate": 0.0, "out_of_turn_attempts": 0, "tokens": 1240 },
    "O":        { "rpva": 1.5, "invalid_rate": 0.05, "out_of_turn_attempts": 1, "tokens": 1340 },
    "Observer": { "rpva": null, "invalid_rate": 1.0, "out_of_turn_attempts": 4, "tokens": 890,
                  "rejected_at_turn": 1, "post_rejection_attempts": 4 }
  }
}
```

JSON files are git-trackable, jq-queryable, and trivially importable into SQLite when cross-cell analysis arrives.

---

## Project layout changes

```
experiments/
├── orchestrator/
│   ├── TicTacToe.Orchestrator.fsproj
│   ├── Program.fs                    ← matrix selector, launches Orchestrator MailboxProcessor
│   ├── Types.fs                      ← Variant, Persona, CellSpec, AgentConfig, transcripts, metrics
│   ├── Personas.fs                   ← persona system prompts as F# data (replaces markdown loading)
│   ├── Orchestrator.fs               ← top-level MailboxProcessor (matrix iteration, lifecycle)
│   ├── Agent.fs                      ← per-agent MailboxProcessor (turn loop)
│   ├── AnthropicClient.fs            ← existing, modified for cleaner integration
│   ├── McpClient.fs                  ← NEW: wraps ModelContextProtocol .NET SDK
│   ├── ServerProcess.fs              ← existing, modified to set env vars from CellSpec
│   ├── ServerLogTail.fs              ← NEW: tails JSONL request logs, filters by game_id
│   ├── Metrics.fs                    ← rewritten for per-agent metrics + role attribution
│   ├── Persistence.fs                ← NEW: writes per-cell JSON, manifest aggregation
│   └── Matrices/
│       ├── Smoke.fs                  ← smoke matrix CellSpec list
│       └── F0.fs                     ← (later)
├── personas/                         ← retained as human-readable docs (no longer parsed at runtime)
└── results/                          ← per-matrix, per-cell directories

src/
├── TicTacToe.Engine/                 ← unchanged
├── TicTacToe.Web/                    ← env-var config + structured logging added
├── TicTacToe.Web.Simple/             ← env-var config + structured logging added
└── TicTacToe.Mcp/                    ← NEW (if option A chosen): MCP server wrapping engine for E_RPC
```

Files removed:
- `experiments/orchestrator/HttpAgent.fs` — superseded by `Agent.fs` (single agent loop, MCP-driven)
- `experiments/orchestrator/RpcAgent.fs` — superseded by `Agent.fs` + `TicTacToe.Mcp` (or absorbed into Agent.fs if option B)
- `experiments/orchestrator/Classifier.fs` — outcome/strategy classification moves to Layer 3 (server logs are the truth)
- `experiments/orchestrator/Runner.fs` — superseded by `Orchestrator.fs`
- `experiments/scripts/smoke.ps1` — replaced by `dotnet run -- smoke`

---

## What G0 looks like after this redesign

G0 re-runs against the smoke matrix above. Pass criteria, scaled to the new design:

- All cells exit cleanly (orchestrator does not crash, all agent processes terminate)
- Per-cell directories produced with all four files present
- `metrics.json` populated for every cell with valid `outcome` and `role_assignment`
- Beginner-vs-Beginner-vs-Beginner cells on Proto and Simple complete with X or O winning or a draw
- E_RPC cells show free-form play (single agent makes moves for both X and O); finding logged
- Beginner-Beginner-Chaos cells on Proto/Simple: chaos agent ends up rejected, has high `out_of_turn_attempts`
- E_RPC + Chaos: chaos agent succeeds at making moves it should not be allowed to make; finding logged

Smoke is not measuring whether RPVA is good — only that the harness, per-agent metrics, role attribution, server logs, and persistence layer all work end-to-end.

---

## Open design decisions (to resolve before implementation plan)

1. **E_RPC tool delivery**: A (TicTacToe.Mcp), B (in-process), or C (drop E_RPC). Recommended: A.
2. **Browser context isolation**: One MCP playwright server per agent (separate processes, separate contexts) vs one shared server with separate contexts. Recommended: separate processes for clean isolation.
3. **Persona storage**: F# data structures (proposed) vs keeping markdown files. Markdown files become docs only.
4. **Observer tool set**: Same MCP servers as players, or a reduced set? Proposed: same — observer must discover its role through interaction; restricting tools would leak the role.
5. **Server log format details**: which exact fields, JSONL vs other. The list above is a proposal to refine during implementation.

---

## Acceptance criteria

- [ ] Design document committed to `docs/superpowers/specs/`
- [ ] User reviews and approves before implementation plan is written
- [ ] All open design decisions resolved (in spec or as follow-up issues)
- [ ] Implementation plan created via `superpowers:writing-plans` after approval
