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
    Model: string
    InitialGames: int
    MaxGames: int
    MaxTurnsPerAgent: int
    McpServers: McpServerConfig list
    Temperature: float
}

type AgentConfig = {
    Id: string          // "agent-1" | "agent-2" | "agent-3"
    Persona: Persona
    Model: string
    BaseUrl: string
    McpServers: McpServerConfig list
    InitialMessage: string option  // if Some, overrides the URL/ERPC default prompt
    MaxTurns: int
    Temperature: float
}

// ── Instrumentation: Layer 2 (tool calls) ────────────────────────────────────

type ToolCallRecord = {
    [<JsonPropertyName("tool_name")>] ToolName: string
    [<JsonPropertyName("input")>] Input: string
    [<JsonPropertyName("output")>] Output: string option
    [<JsonPropertyName("error")>] Error: string option
    [<JsonPropertyName("duration_ms")>] DurationMs: int
    [<JsonPropertyName("timestamp")>] Timestamp: DateTimeOffset
}

// ── Instrumentation: Layer 1 (LLM turns) ─────────────────────────────────────

type LlmTurn = {
    [<JsonPropertyName("turn_index")>] TurnIndex: int
    [<JsonPropertyName("stop_reason")>] StopReason: string
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
    [<JsonPropertyName("rpva")>] Rpva: float option
    [<JsonPropertyName("invalid_rate")>] InvalidRate: float
    [<JsonPropertyName("out_of_turn_attempts")>] OutOfTurnAttempts: int
    [<JsonPropertyName("tokens")>] Tokens: int
}

type RoleAssignment = {
    [<JsonPropertyName("agent_id")>] AgentId: string
    [<JsonPropertyName("role")>] Role: string
}

type CellMetrics = {
    [<JsonPropertyName("cell_id")>] CellId: string
    [<JsonPropertyName("outcome")>] Outcome: string
    [<JsonPropertyName("completion_signal")>] CompletionSignal: string
    [<JsonPropertyName("duration_seconds")>] DurationSeconds: float
    [<JsonPropertyName("role_assignments")>] RoleAssignments: RoleAssignment list
    [<JsonPropertyName("per_agent")>] PerAgent: Map<string, PerAgentMetrics>
}

type CellResult = {
    CellSpec: CellSpec
    Transcripts: Map<string, AgentTranscript>
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

// ── Backend type (kept for LlmClient.fs) ─────────────────────────────────────

type Backend = Anthropic | OpenAiCompat

// ── Helpers ───────────────────────────────────────────────────────────────────

module Variant =
    let toString = function Proto -> "proto" | Simple -> "simple" | ERPC -> "erpc"
    let defaultPort = function Proto -> 5228 | Simple -> 5328 | ERPC -> 0
    let projectPath = function
        | Proto  -> "src/TicTacToe.Web/TicTacToe.Web.fsproj"
        | Simple -> "src/TicTacToe.Web.Simple/TicTacToe.Web.Simple.fsproj"
        | ERPC   -> ""

module Backend =
    let toString = function Anthropic -> "anthropic" | OpenAiCompat -> "openai"

    let autoDetect () =
        let workerUrl = Environment.GetEnvironmentVariable("WORKER_BASE_URL")
        if not (String.IsNullOrEmpty(workerUrl)) then OpenAiCompat
        else Anthropic
