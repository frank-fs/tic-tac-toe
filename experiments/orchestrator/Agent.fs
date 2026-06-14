module TicTacToe.Orchestrator.Agent

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.LlmClient
open TicTacToe.Orchestrator.McpClient

let private buildTranscript (agentId: string) (persona: Persona) (turns: LlmTurn list) (aborted: bool) : AgentTranscript =
    { AgentId = agentId; PersonaName = persona.Name; LlmTurns = turns; Aborted = aborted }

let private isTerminalErpcState (output: string) : bool =
    try
        let obj = JsonNode.Parse(output) :?> JsonObject
        let mutable s: JsonNode = null
        let mutable e: JsonNode = null
        (obj.TryGetPropertyValue("status", &s) &&
         let v = s.GetValue<string>() in v = "won" || v = "draw")
        || (obj.TryGetPropertyValue("error", &e) &&
            e.GetValue<string>() = "GameNotFound")
    with _ -> false

// Playwright MCP returns snapshot file references like [Snapshot](path.yml).
// Inline the file content so the LLM sees the accessibility tree directly.
let private inlineSnapshots (output: string) : string =
    Regex.Replace(output, @"\[Snapshot\]\(([^)]+\.yml)\)", fun m ->
        let path = m.Groups.[1].Value
        try
            let content = File.ReadAllText(path)
            $"Accessibility tree:\n```\n{content}\n```"
        with _ -> m.Value)

let private executeTurn
    (config: AgentConfig)
    (backend: Backend)
    (mcpClients: McpClientSet)
    (messages: JsonArray)
    (turnIndex: int)
    (currentTurns: LlmTurn list)
    : Task<LlmTurn list * bool> =
    task {
        let tools = mcpClients.GetAllTools()
        let! result =
            runTurn backend config.Model config.Temperature
                (Some config.Persona.SystemPrompt) tools config.ForceToolUse messages config.Cancellation

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
            if config.ForceToolUse then
                let nudge =
                    match surfaceOf config.McpServers with
                    | Rpc -> "The game is in progress. Call get_state to see the current board, then make your move."
                    | Browser -> "The game is in progress. Take a fresh snapshot to see the current board, then act."
                    | Http ->
                        match config.Variant with
                        | Simple -> "The game is in progress. Request the current page to see the board, then take your next action."
                        | _ -> "The game is in progress. Request the page (and drain the event stream) to see the board, then take your next action."
                appendUserText messages nudge |> ignore
                return (currentTurns @ [turn], true)
            else
                return (currentTurns @ [turn], false)

        | ToolCalls(calls, inp, out) ->
            appendAssistantToolUse backend messages calls |> ignore

            let sw = Stopwatch()
            let toolCallRecords = System.Collections.Generic.List<ToolCallRecord>()
            let toolResults = System.Collections.Generic.List<string * string>()

            for call in calls do
                let args =
                    call.Input
                    |> Seq.map (fun kv -> kv.Key, kv.Value :> JsonNode)
                    |> Map.ofSeq

                sw.Restart()
                let! rawOutput = mcpClients.CallToolAsync(call.Name, args, config.Cancellation)
                sw.Stop()
                let output = inlineSnapshots rawOutput

                toolCallRecords.Add({
                    ToolName = call.Name
                    Input = call.Input.ToJsonString()
                    Output = Some output
                    Error = None
                    DurationMs = int sw.ElapsedMilliseconds
                    Timestamp = DateTimeOffset.UtcNow
                })
                toolResults.Add(call.Id, output)

            appendToolResults backend messages (toolResults |> Seq.toList) |> ignore

            let turn = {
                TurnIndex = turnIndex
                StopReason = "tool_use"
                InputTokens = inp
                OutputTokens = out
                ToolCalls = toolCallRecords |> Seq.toList
                TextOutput = None
                Timestamp = DateTimeOffset.UtcNow
            }
            let gameEnded = toolCallRecords |> Seq.exists (fun r -> isTerminalErpcState (r.Output |> Option.defaultValue ""))
            return (currentTurns @ [turn], not gameEnded)
    }

/// sharedClients: if Some, agent uses these and does NOT dispose them (orchestrator owns lifecycle).
/// If None, agent creates its own McpClientSet and disposes on stop.
let createAgent (config: AgentConfig) (sharedClients: McpClientSet option) : MailboxProcessor<AgentMsg> =
    MailboxProcessor.Start(fun inbox ->
        let backend = Backend.autoDetect()
        let messages = JsonArray()
        appendUserText messages config.InitialMessage |> ignore

        let mutable turns: LlmTurn list = []
        let mutable aborted = false

        let disposeIfOwned (clientsOpt: McpClientSet option) =
            match clientsOpt with
            | Some c when sharedClients.IsNone -> (c :> IDisposable).Dispose()
            | _ -> ()

        // Idle until the orchestrator collects the transcript. Replies Done=true so the
        // cell-wait poll can short-circuit. Holds the real accumulated turns.
        let rec waitStop (clientsOpt: McpClientSet option) =
            async {
                let! msg = inbox.Receive()
                match msg with
                | StopAgent reply ->
                    disposeIfOwned clientsOpt
                    reply.Reply(buildTranscript config.Id config.Persona turns aborted)
                | GetSnapshot reply ->
                    reply.Reply({ AgentId = config.Id; TurnIndex = List.length turns; Aborted = aborted; Done = true })
                    return! waitStop clientsOpt
                | StartAgent ->
                    return! waitStop clientsOpt
            }

        // One turn, guarded: cell-cap cancellation or an LLM/tool fault aborts the agent
        // with its real turns intact — never a fabricated empty transcript. Returns keepGoing.
        let runOneTurn (clients: McpClientSet) (turnIndex: int) : Async<bool> =
            async {
                try
                    let! (newTurns, keepGoing) =
                        executeTurn config backend clients messages turnIndex turns |> Async.AwaitTask
                    turns <- newTurns
                    return keepGoing
                with
                | :? OperationCanceledException ->
                    aborted <- true
                    return false
                | ex ->
                    eprintfn $"[agent {config.Id}] turn {turnIndex} faulted: {ex.Message}"
                    aborted <- true
                    return false
            }

        let rec runLoop (turnIndex: int) (clients: McpClientSet) =
            async {
                let! maybeStop = inbox.TryReceive(timeout = 0)
                match maybeStop with
                | Some (StopAgent reply) ->
                    disposeIfOwned (Some clients)
                    reply.Reply(buildTranscript config.Id config.Persona turns aborted)
                | Some (GetSnapshot reply) ->
                    reply.Reply({ AgentId = config.Id; TurnIndex = turnIndex; Aborted = aborted; Done = false })
                    return! runLoop turnIndex clients
                | _ when turnIndex >= config.MaxTurns ->
                    aborted <- true
                    return! waitStop (Some clients)
                | _ ->
                    let! keepGoing = runOneTurn clients turnIndex
                    if keepGoing then return! runLoop (turnIndex + 1) clients
                    else return! waitStop (Some clients)
            }

        async {
            let! msg = inbox.Receive()
            match msg with
            | StartAgent ->
                let! clientsOpt =
                    match sharedClients with
                    | Some c -> async { return Some c }
                    | None ->
                        async {
                            try
                                let c = new McpClientSet(config.McpServers)
                                do! c.InitializeAsync(config.Cancellation) |> Async.AwaitTask
                                return Some c
                            with ex ->
                                eprintfn $"[agent {config.Id}] MCP init failed: {ex.Message}"
                                return None
                        }
                match clientsOpt with
                | Some clients -> return! runLoop 0 clients
                | None ->
                    aborted <- true
                    return! waitStop None
            | StopAgent reply ->
                reply.Reply(buildTranscript config.Id config.Persona [] false)
            | GetSnapshot reply ->
                reply.Reply({ AgentId = config.Id; TurnIndex = 0; Aborted = false; Done = false })
        })
