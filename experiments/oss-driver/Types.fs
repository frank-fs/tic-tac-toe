module TicTacToe.OssDriver.Types

open System

/// LLM backend wire format. OpenRouter/Groq/Together/LM-Studio are all OpenAiCompat;
/// Anthropic native kept for the haiku reference path. (Salvaged from the orchestrator.)
type Backend =
    | Anthropic
    | OpenAiCompat

module Backend =
    /// WORKER_BASE_URL set ⇒ an OpenAI-compatible endpoint (the OSS path); else Anthropic.
    let autoDetect () =
        if not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "WORKER_BASE_URL"))
        then OpenAiCompat
        else Anthropic

/// A persona is ONLY the mastery/character layer — the stateful protocol "floor" is
/// supplied by the driver, not here. (Cleaned of the old browsegrab ref-id procedure.)
type Persona = { Name: string; Guidance: string }
