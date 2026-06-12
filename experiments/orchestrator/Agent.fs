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
            return (currentTurns @ [turn], true)
    }

let createAgent (config: AgentConfig) : MailboxProcessor<AgentMsg> =
    MailboxProcessor.Start(fun inbox ->
        let backend = Backend.autoDetect()
        let messages = JsonArray()
        appendUserText messages $"Here is a URL: {config.BaseUrl}" |> ignore

        let mutable turns: LlmTurn list = []
        let mutable aborted = false

        let rec runLoop (turnIndex: int) (clients: McpClientSet) =
            async {
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
                    let rec waitStop () =
                        async {
                            let! msg = inbox.Receive()
                            match msg with
                            | StopAgent reply ->
                                (clients :> IDisposable).Dispose()
                                reply.Reply(buildTranscript config.Id config.Persona turns true)
                            | GetSnapshot reply ->
                                reply.Reply({ AgentId = config.Id; TurnIndex = turnIndex; Aborted = true })
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
                                    (clients :> IDisposable).Dispose()
                                    reply.Reply(buildTranscript config.Id config.Persona turns aborted)
                                | GetSnapshot reply ->
                                    reply.Reply({ AgentId = config.Id; TurnIndex = List.length turns; Aborted = false })
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
                let clients = McpClientSet(config.McpServers)
                do! clients.InitializeAsync() |> Async.AwaitTask
                return! runLoop 0 clients
            | StopAgent reply ->
                reply.Reply(buildTranscript config.Id config.Persona [] false)
            | GetSnapshot reply ->
                reply.Reply({ AgentId = config.Id; TurnIndex = 0; Aborted = false })
        })
