module TicTacToe.Orchestrator.Agent

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
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
                (Some config.Persona.SystemPrompt) tools config.ForceToolUse messages

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
                    if String.IsNullOrEmpty(config.BaseUrl)
                    then "The game is still in progress. Call get_state or make_move to continue."
                    else "Continue interacting with the web page. Use browser tools to take the next action."
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
                let! rawOutput = mcpClients.CallToolAsync(call.Name, args)
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
        let initialMsg =
            match config.InitialMessage with
            | Some msg -> msg
            | None ->
                if String.IsNullOrEmpty(config.BaseUrl) then
                    "You are playing tic-tac-toe using MCP tools. First call list_games to check for an active game. " +
                    "If a game exists, use its gameId and call get_board to see the state, then make_move when it is your turn. " +
                    "If no game exists, call list_games again up to 3 times before giving up. " +
                    "Only call new_game if list_games is still empty after all retries. " +
                    "Once in a game, keep making moves until the game is over."
                else
                    $"Here is a URL: {config.BaseUrl}"
        appendUserText messages initialMsg |> ignore

        let mutable turns: LlmTurn list = []
        let mutable aborted = false

        let rec runLoop (turnIndex: int) (clients: McpClientSet) =
            async {
                let! maybeStop = inbox.TryReceive(timeout = 0)
                match maybeStop with
                | Some (StopAgent reply) ->
                    if sharedClients.IsNone then (clients :> IDisposable).Dispose()
                    reply.Reply(buildTranscript config.Id config.Persona turns aborted)

                | Some (GetSnapshot reply) ->
                    reply.Reply({ AgentId = config.Id; TurnIndex = turnIndex; Aborted = aborted; Done = false })
                    return! runLoop turnIndex clients

                | _ when turnIndex >= config.MaxTurns ->
                    aborted <- true
                    let rec waitStop () =
                        async {
                            let! msg = inbox.Receive()
                            match msg with
                            | StopAgent reply ->
                                if sharedClients.IsNone then (clients :> IDisposable).Dispose()
                                reply.Reply(buildTranscript config.Id config.Persona turns true)
                            | GetSnapshot reply ->
                                reply.Reply({ AgentId = config.Id; TurnIndex = turnIndex; Aborted = true; Done = true })
                                return! waitStop()
                            | StartAgent ->
                                return! waitStop()
                        }
                    return! waitStop()

                | _ ->
                    let! (newTurns, keepGoing) =
                        executeTurn config backend clients messages turnIndex turns
                        |> Async.AwaitTask
                    turns <- newTurns
                    if keepGoing then
                        return! runLoop (turnIndex + 1) clients
                    else
                        let rec waitStop () =
                            async {
                                let! msg = inbox.Receive()
                                match msg with
                                | StopAgent reply ->
                                    if sharedClients.IsNone then (clients :> IDisposable).Dispose()
                                    reply.Reply(buildTranscript config.Id config.Persona turns aborted)
                                | GetSnapshot reply ->
                                    reply.Reply({ AgentId = config.Id; TurnIndex = List.length turns; Aborted = false; Done = true })
                                    return! waitStop()
                                | StartAgent ->
                                    return! waitStop()
                            }
                        return! waitStop()
            }

        async {
            let! msg = inbox.Receive()
            match msg with
            | StartAgent ->
                let! clients =
                    match sharedClients with
                    | Some c -> async { return c }
                    | None ->
                        async {
                            let c = new McpClientSet(config.McpServers)
                            do! c.InitializeAsync() |> Async.AwaitTask
                            return c
                        }
                return! runLoop 0 clients
            | StopAgent reply ->
                reply.Reply(buildTranscript config.Id config.Persona [] false)
            | GetSnapshot reply ->
                reply.Reply({ AgentId = config.Id; TurnIndex = 0; Aborted = false; Done = false })
        })
