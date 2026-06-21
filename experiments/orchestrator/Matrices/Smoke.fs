module TicTacToe.Orchestrator.Matrices.Smoke

open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Personas

let private mcpServer = {
    Name = "tictactoe-mcp"
    Command = "dotnet"
    Arguments = [| "run"; "--project"; "experiments/mcp-rpc/"; "--no-build" |]
    Env = [||]
}

// Browser surfaces for the tool A/B/C: render JS + project the accessibility tree.
// Isolated profile per process so the three agents are distinct players.
let private playwrightServer = {
    Name = "playwright"
    Command = "npx"
    Arguments = [| "@playwright/mcp@latest"; "--headless"; "--isolated" |]
    Env = [||]
}

let private chromeDevtoolsServer = {
    Name = "chrome-devtools"
    Command = "npx"
    Arguments = [| "chrome-devtools-mcp@latest"; "--headless"; "--isolated" |]
    Env = [||]
}

// Token-efficient a11y-tree browser for local LLMs: stable e1/e2 refs + Playwright
// auto-wait, no vision, no network-envelope tool. Candidate for ERPC-like reliable
// interaction (select by stable ref, re-snapshot for state). Installed via uv tool.
let private browsegrabServer = {
    Name = "browsegrab"
    Command = "browsegrab-mcp"
    Arguments = [||]
    Env = [||]
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
    cell "smoke-proto-bbb"  Proto  beginner beginner beginner [browsegrabServer]
    cell "smoke-simple-bbb" Simple beginner beginner beginner [browsegrabServer]
    cell "smoke-erpc-bbb"   ERPC   beginner beginner beginner [mcpServer]
    cell "smoke-erpc-bbc"   ERPC   beginner beginner chaos    [mcpServer]
    cell "smoke-proto-bbc"  Proto  beginner beginner chaos    [browsegrabServer]
    cell "smoke-simple-bbc" Simple beginner beginner chaos    [browsegrabServer]
]

// Tool A/B/C on the SAME app (Proto): do the different browser tools agree on the
// discovery/interaction outcome, or does the tool itself move the result?
let protoAb : CellSpec list = [
    cell "proto-ab-browsegrab" Proto beginner beginner beginner [browsegrabServer]
    cell "proto-ab-playwright" Proto beginner beginner beginner [playwrightServer]
    cell "proto-ab-cdt"        Proto beginner beginner beginner [chromeDevtoolsServer]
]

// Repeat each cell n times (suffix -01..-0n) to sample a distribution rather than a
// single point: small models aren't deterministic, so a one-off fumble (e.g. miscopying
// a 36-char UUID) shouldn't dominate the read. Interleaved by sample (outer loop = i)
// so backend warm-up / time drift spreads evenly across both arms.
let private samples (n: int) (cells: CellSpec list) : CellSpec list =
    [ for i in 1 .. n do
        for c in cells -> { c with Id = sprintf "%s-%02d" c.Id i } ]

// Step 1 of the tool hunt: is browsegrab on Simple equivalent to ERPC (same outcome,
// differing only in context size), with low variance across the sample? Three agents
// retained — the two-player cross-browser refresh (X acts, O's separate browser must
// re-read to see it) is the core thing under test; the third probes the blocked/observer
// path. Simple has no SSE re-render, so this isolates the tool baseline before Proto.
let simpleAb : CellSpec list =
    samples 5 [
        cell "simple-ab-browsegrab" Simple beginner beginner beginner [browsegrabServer]
        cell "simple-ab-erpc"       ERPC   beginner beginner beginner [mcpServer]
    ]

// L3 instructed baseline: fully-scripted X/O + observer. Proves the harness+tool can run a
// full game to completion (GameOver) when the agent isn't the variable. Role-enforced ERPC
// (per-request _meta identity) vs Simple's session-bound roles. 1×each first to confirm the
// completion path fires, then sample for a completion rate.
let instructed : CellSpec list = [
    cell "instructed-browsegrab" Simple scriptedX scriptedO observer [browsegrabServer]
    cell "instructed-erpc"       ERPC   scriptedX scriptedO observer [mcpServer]
]
