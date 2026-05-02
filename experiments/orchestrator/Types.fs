module TicTacToe.Orchestrator.Types

open System.Text.Json
open System.Text.Json.Serialization

// ── CLI config ────────────────────────────────────────────────────────────────

type Variant = Proto | Simple

type ModelId = Haiku | Sonnet | Opus | Custom of string

type Persona = Beginner | Expert | Chaos

type Setup = E0 | E1 | ERPC

/// Which wire format to use when calling the LLM.
/// Anthropic: POST /v1/messages, Anthropic auth headers, tool_use blocks.
/// OpenAiCompat: POST /v1/chat/completions, Bearer auth, OpenAI tool_calls format (LM Studio, Moonshot, etc.)
type Backend = Anthropic | OpenAiCompat

type RunConfig = {
    Commit: string
    Variant: Variant
    Model: ModelId
    Persona: Persona
    Setup: Setup
    Games: int
    Output: string
    Temperature: float
    Backend: Backend
}

// ── Transcript ────────────────────────────────────────────────────────────────

type OutcomeTag =
    | ValidAction
    | InvalidAction
    | Discovery
    | Retry
    | Abandoned

type StrategyTag =
    | HtmlFollow
    | SpecFollow
    | BlindPost
    | RetryStrategy

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

and TranscriptEntryConverter() =
    inherit JsonConverter<TranscriptEntry>()
    override _.Write(writer, value, opts) =
        match value with
        | Http e -> JsonSerializer.Serialize(writer, e, opts)
        | Tool e -> JsonSerializer.Serialize(writer, e, opts)
    override _.Read(_, _, _) = failwith "not used"

// ── Metrics ───────────────────────────────────────────────────────────────────

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

// ── JSON converters for F# DUs ────────────────────────────────────────────────

type OutcomeTagConverter() =
    inherit JsonConverter<OutcomeTag>()
    override _.Write(writer, value, _) =
        writer.WriteStringValue(
            match value with
            | ValidAction -> "ValidAction" | InvalidAction -> "InvalidAction"
            | Discovery -> "Discovery" | Retry -> "Retry" | Abandoned -> "Abandoned")
    override _.Read(reader, _, _) =
        match reader.GetString() with
        | "ValidAction" -> ValidAction | "InvalidAction" -> InvalidAction
        | "Discovery" -> Discovery | "Retry" -> Retry | "Abandoned" -> Abandoned
        | s -> failwithf "Unknown OutcomeTag: %s" s

type StrategyTagConverter() =
    inherit JsonConverter<StrategyTag>()
    override _.Write(writer, value, _) =
        writer.WriteStringValue(
            match value with
            | HtmlFollow -> "HtmlFollow" | SpecFollow -> "SpecFollow"
            | BlindPost -> "BlindPost" | RetryStrategy -> "RetryStrategy")
    override _.Read(reader, _, _) =
        match reader.GetString() with
        | "HtmlFollow" -> HtmlFollow | "SpecFollow" -> SpecFollow
        | "BlindPost" -> BlindPost | "RetryStrategy" -> RetryStrategy
        | s -> failwithf "Unknown StrategyTag: %s" s

// ── Helpers ───────────────────────────────────────────────────────────────────

module ModelId =
    let toApiString = function
        | Haiku -> "claude-haiku-4-5-20251001"
        | Sonnet -> "claude-sonnet-4-6"
        | Opus -> "claude-opus-4-7"
        | Custom s -> s
    let toString = function
        | Haiku -> "haiku" | Sonnet -> "sonnet" | Opus -> "opus"
        | Custom s -> s

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

module Backend =
    let toString = function Anthropic -> "anthropic" | OpenAiCompat -> "openai"

    /// Auto-detect backend from environment variables.
    /// WORKER_BASE_URL set → OpenAiCompat (LM Studio OpenAI endpoint, Moonshot, etc.)
    /// Otherwise → Anthropic (real API or LM Studio Anthropic endpoint via ANTHROPIC_BASE_URL)
    let autoDetect () =
        let workerUrl = System.Environment.GetEnvironmentVariable("WORKER_BASE_URL")
        if not (System.String.IsNullOrEmpty(workerUrl)) then OpenAiCompat
        else Anthropic
