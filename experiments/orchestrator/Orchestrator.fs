module TicTacToe.Orchestrator.Orchestrator

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading
open System.Text.Json.Nodes
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics
open TicTacToe.Orchestrator.Persistence
open TicTacToe.Orchestrator.ServerLogTail
open TicTacToe.Orchestrator.McpClient
open TicTacToe.Orchestrator.ServerProcess
open TicTacToe.Orchestrator.Agent

// Fail-fast: hard wall-clock cap per cell. Independent timeouts (MCP call 120s,
// StopAgent 60s) all sit under this.
[<Literal>]
let private cellWaitSeconds = 300

let private makeAgentConfig (cell: CellSpec) (slot: int) (persona: Persona) (baseUrl: string) (initialMessage: string) (identityToken: string option) (cancellation: CancellationToken) : AgentConfig =
    { Id = $"agent-{slot}"
      Persona = persona
      Model = cell.Model
      Variant = cell.Variant
      BaseUrl = baseUrl
      McpServers = cell.McpServers
      InitialMessage = initialMessage
      ForceToolUse = true
      MaxTurns = cell.MaxTurnsPerAgent
      Temperature = cell.Temperature
      Cancellation = cancellation
      IdentityToken = identityToken }

// Protocol-general literacy for the HTTP agents (no game-specific coaching): how to
// read the uniform interface their http_request tool surfaces.
let private httpHint =
    "Use the http_request tool (method, url, headers, body). The response status code " +
    "tells you whether your action was accepted (2xx) or rejected (4xx); the Content-Type " +
    "tells you how to read the body and which controls are available. To submit a form, set " +
    "the Content-Type header to application/x-www-form-urlencoded and put the form's fields " +
    "in body, e.g. name=value&name2=value2."

// Protocol-general literacy for a browser agent: read the rendered page semantically via
// snapshot, act, then re-snapshot to read the new state. Network inspection is offered
// conditionally — some browser tools (browsegrab) expose no network log.
let private browserHint =
    "Navigate to the page, then take a snapshot to read its accessibility tree (the controls, " +
    "their labels and states). Click a control to act, then take a new snapshot to see the result — " +
    "the updated controls and their states reflect whether your action was accepted. If your tools " +
    "expose the network log, you can also inspect the HTTP status (2xx accepted, 4xx rejected)."

let private slotMessage (surface: AgentSurface) (variant: Variant) (baseUrl: string) : string =
    match surface with
    | Rpc ->
        "The game server is ready. Call list_games to find the game, then join_game to claim " +
        "your player role (X or O) — it returns a playerToken. Read the board with get_state. " +
        "To move, call make_move with your playerToken and a position; it only succeeds on your " +
        "turn. get_state only reflects new moves when you call it again."
    | Browser ->
        $"The game is a web app at {baseUrl}. {browserHint}"
    | Http ->
        match variant with
        | Simple ->
            $"The game server is at {baseUrl}. {httpHint} Re-request a resource to see new moves."
        | _ ->
            $"The game server is at {baseUrl}. {httpHint} If a response is text/event-stream it " +
            $"stays open — re-request that URL to drain new moves as they are pushed."

// HTTP cells: stop waiting on the FIRST of — a GameOver in the server log (a real
// win/draw), or all agents having given up (hit MaxTurns) — capped at maxWaitSeconds.
// Without the agents-done check a dead cell idles the full cap. GetSnapshot is polled
// with a short timeout so a wedged agent can't block the poll itself.
let private waitForGameOverOrAgentsDone
    (logPath: string) (agents: MailboxProcessor<AgentMsg> list) (maxWaitSeconds: int) : Async<bool> =
    async {
        let tail = startTail logPath
        let deadline = DateTimeOffset.UtcNow.AddSeconds(float maxWaitSeconds)
        let rec poll () =
            async {
                if DateTimeOffset.UtcNow >= deadline then return false
                else
                    let gameOver = tail.GetEvents() |> List.exists (function GameOver _ -> true | _ -> false)
                    if gameOver then return true
                    else
                        let! snapshots =
                            agents
                            |> List.map (fun a -> a.PostAndTryAsyncReply((fun r -> GetSnapshot r), timeout = 2000))
                            |> Async.Parallel
                        if snapshots |> Array.forall (fun s -> s |> Option.map (fun x -> x.Done) |> Option.defaultValue false)
                        then return false
                        else
                            do! Async.Sleep(1000)
                            return! poll ()
            }
        return! poll ()
    }

// Polls agents via GetSnapshot (no shared MCP access) until all have entered waitStop.
// Agents self-terminate when isTerminalErpcState detects a game-over tool result.
let private waitForAgentsDone (agents: MailboxProcessor<AgentMsg> list) (maxWaitSeconds: int) : Async<bool> =
    async {
        let deadline = DateTimeOffset.UtcNow.AddSeconds(float maxWaitSeconds)
        let rec poll () =
            async {
                if DateTimeOffset.UtcNow >= deadline then return true
                else
                    let! snapshots =
                        agents
                        |> List.map (fun a -> a.PostAndTryAsyncReply((fun r -> GetSnapshot r), timeout = 2000))
                        |> Async.Parallel
                    if snapshots |> Array.forall (fun s -> s |> Option.map (fun x -> x.Done) |> Option.defaultValue false) then return true
                    else
                        do! Async.Sleep(1000)
                        return! poll ()
            }
        return! poll ()
    }

// Scans agent transcripts for the last tool result containing a terminal game status.
// Used for ERPC where the game is removed from engine state once complete.
let private erpcFinalEvent (gameId: string) (transcripts: AgentTranscript list) : ServerLogEvent list =
    let terminal =
        transcripts
        |> List.collect (fun t -> t.LlmTurns)
        |> List.collect (fun turn -> turn.ToolCalls)
        |> List.tryPick (fun tc ->
            let out = tc.Output |> Option.defaultValue ""
            try
                let obj = JsonNode.Parse(out) :?> JsonObject
                let mutable statusNode: JsonNode = null
                if obj.TryGetPropertyValue("status", &statusNode) then
                    let status = statusNode.GetValue<string>()
                    if status = "won" || status = "draw" then
                        let moveCount =
                            try
                                let board = obj.["board"] :?> JsonArray
                                board
                                |> Seq.cast<JsonNode>
                                |> Seq.filter (fun n -> try n <> null && n.GetValue<string>() <> "" with _ -> false)
                                |> Seq.length
                            with _ -> 0
                        Some (status, moveCount)
                    else None
                else None
            with _ -> None)
    match terminal with
    | Some (status, moveCount) -> [GameOver(gameId, status, moveCount, DateTimeOffset.UtcNow)]
    | None -> []

let private runCell (repoRoot: string) (cell: CellSpec) : Async<CellResult> =
    async {
        let cellStart = DateTimeOffset.UtcNow
        printfn $"[cell] starting: {cell.Id}"

        // Master fail-fast signal: cancelled at the wall-clock cap to abort in-flight LLM/MCP
        // calls so agents return their real accumulated turns instead of a fabricated placeholder.
        use cellCts = new CancellationTokenSource()

        let logPath = Path.Combine(repoRoot, "experiments", "results", cell.Id, "server-requests.jsonl")
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)) |> ignore

        let! serverHandleOpt =
            if cell.Variant = ERPC then async { return None }
            else
                async {
                    let! handle = startServerForCell repoRoot cell |> Async.AwaitTask
                    return Some handle
                }

        let baseUrl =
            serverHandleOpt
            |> Option.map (fun h -> h.BaseUrl)
            |> Option.defaultValue ""

        // ERPC: one shared MCP server process for all 3 agents so they see the same game state.
        // The shared process is still correct for shared state; identities are now per-REQUEST —
        // each agent's tool calls carry its own token in MCP `_meta.identityToken` (see makeAgentConfig
        // / McpClientSet.CallToolAsync), so distinct seats are bound over the one stdio connection.
        // Pre-create a game so agents find it immediately and get explicit role assignments.
        let! sharedClientsOpt =
            if cell.Variant = ERPC then
                async {
                    let clients = new McpClientSet(cell.McpServers)
                    do! clients.InitializeAsync(cellCts.Token) |> Async.AwaitTask
                    return Some clients
                }
            else async { return None }

        let! erpcGameId =
            match sharedClientsOpt with
            | None -> async { return None }
            | Some clients ->
                async {
                    let! json = clients.CallToolAsync("new_game", Map.empty, cellCts.Token) |> Async.AwaitTask
                    let gameId =
                        try (JsonNode.Parse(json) :?> JsonObject).["gameId"].GetValue<string>()
                        with _ -> failwith $"ERPC pre-create game failed: {json}"
                    return Some gameId
                }

        // ERPC: mint a distinct server identity per agent up front. Each token rides every
        // tool call that agent makes via `_meta` (transparent to the LLM), binding it to its
        // own seat over the shared connection. Non-ERPC arms get None.
        let mintToken (clients: McpClientSet) : Async<string> =
            async {
                let! json = clients.CallToolAsync("authenticate", Map.empty, None, cellCts.Token) |> Async.AwaitTask
                try return (JsonNode.Parse(json) :?> JsonObject).["token"].GetValue<string>()
                with _ -> return failwith $"ERPC authenticate failed: {json}"
            }

        let! slotTokens =
            match sharedClientsOpt with
            | None -> async { return Map.ofList [1, None; 2, None; 3, None] }
            | Some clients ->
                async {
                    let mutable acc = Map.empty
                    for slot in [1; 2; 3] do
                        let! token = mintToken clients
                        acc <- Map.add slot (Some token) acc
                    return acc
                }

        let (p1, p2, p3) = cell.Personas
        let agentsWithMeta =
            [1, p1; 2, p2; 3, p3]
            |> List.map (fun (slot, persona) ->
                let initialMsg = slotMessage (surfaceOf cell.McpServers) cell.Variant baseUrl
                let token = slotTokens |> Map.find slot
                let agent = createAgent (makeAgentConfig cell slot persona baseUrl initialMsg token cellCts.Token) sharedClientsOpt
                slot, persona, agent)
        let agents = agentsWithMeta |> List.map (fun (_, _, a) -> a)

        let sw = Stopwatch.StartNew()

        for agent in agents do agent.Post(StartAgent)

        let! gameOver =
            match cell.Variant with
            | ERPC -> waitForAgentsDone agents cellWaitSeconds
            | _ -> waitForGameOverOrAgentsDone logPath agents cellWaitSeconds

        if gameOver then do! Async.Sleep(5000)

        // Cap reached (or game over): cancel in-flight LLM/MCP calls so each agent unwinds to
        // waitStop and answers StopAgent promptly with its real accumulated turns. The 60s
        // StopAgent budget is now slack — agents respond in well under a second once cancelled.
        // The aborted placeholder below remains only as a last-resort safety.
        cellCts.Cancel()

        let transcriptList = System.Collections.Generic.List<AgentTranscript>()
        for (slot, persona, agent) in agentsWithMeta do
            let! tOpt = agent.PostAndTryAsyncReply((fun r -> StopAgent r), timeout = 60000)
            let t =
                tOpt
                |> Option.defaultValue
                    { AgentId = $"agent-{slot}"; PersonaName = persona.Name; LlmTurns = []; Aborted = true }
            transcriptList.Add(t)

        sw.Stop()

        sharedClientsOpt |> Option.iter (fun c -> (c :> IDisposable).Dispose())

        let transcripts = transcriptList |> Seq.map (fun t -> t.AgentId, t) |> Map.ofSeq

        let erpcEvents =
            match erpcGameId with
            | Some gameId -> erpcFinalEvent gameId (transcriptList |> Seq.toList)
            | None -> []

        let events = (startTail logPath).GetEvents() @ erpcEvents

        let sessionMap = Map.empty
        let roles = resolveRoles events sessionMap

        let durationSeconds = sw.Elapsed.TotalSeconds
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
