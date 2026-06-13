module TicTacToe.Orchestrator.Orchestrator

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics
open TicTacToe.Orchestrator.Persistence
open TicTacToe.Orchestrator.ServerLogTail
open TicTacToe.Orchestrator.McpClient
open TicTacToe.Orchestrator.ServerProcess
open TicTacToe.Orchestrator.Agent

let private makeAgentConfig (cell: CellSpec) (slot: int) (persona: Persona) (baseUrl: string) (initialMessage: string option) : AgentConfig =
    { Id = $"agent-{slot}"
      Persona = persona
      Model = cell.Model
      BaseUrl = baseUrl
      McpServers = cell.McpServers
      InitialMessage = initialMessage
      ForceToolUse = true
      MaxTurns = cell.MaxTurnsPerAgent
      Temperature = cell.Temperature }

let private erpcSlotMessage (slot: int) (gameId: string) : string =
    match slot with
    | 1 ->
        $"You are player X in tic-tac-toe game {gameId}. It is X's turn first. " +
        "Call make_move to play your move. After playing, call get_state to check if it is your turn again (whoseTurn = X). " +
        "Keep playing until the game status is won or draw."
    | 2 ->
        $"You are player O in tic-tac-toe game {gameId}. " +
        "Call get_state to check the board. When whoseTurn = O it is your turn — call make_move. " +
        "Keep polling get_state and playing when it is your turn until the game ends."
    | _ ->
        $"You are observing tic-tac-toe game {gameId}. " +
        "Call get_state periodically to watch the game. Stop when status is won or draw."

let private waitForGameOver (logPath: string) (maxWaitSeconds: int) : Async<bool> =
    async {
        let tail = startTail logPath
        let rec poll attempt =
            async {
                if attempt >= maxWaitSeconds then return false
                else
                    let events = tail.GetEvents()
                    if events |> List.exists (function GameOver _ -> true | _ -> false) then
                        return true
                    else
                        do! Async.Sleep(1000)
                        return! poll (attempt + 1)
            }
        return! poll 0
    }

// Polls agents via GetSnapshot (no shared MCP access) until all have entered waitStop.
// Agents self-terminate when isTerminalErpcState detects a game-over tool result.
let private waitForAgentsDone (agents: MailboxProcessor<AgentMsg> list) (maxWaitSeconds: int) : Async<bool> =
    async {
        let rec poll elapsed =
            async {
                if elapsed >= maxWaitSeconds then return true
                else
                    let! snapshots =
                        agents
                        |> List.map (fun a -> a.PostAndAsyncReply(fun r -> GetSnapshot r))
                        |> Async.Parallel
                    if snapshots |> Array.forall (fun s -> s.Done) then return true
                    else
                        do! Async.Sleep(1000)
                        return! poll (elapsed + 1)
            }
        return! poll 0
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
        // Pre-create a game so agents find it immediately via list_games and get explicit role assignments.
        let! sharedClientsOpt =
            if cell.Variant = ERPC then
                async {
                    let clients = new McpClientSet(cell.McpServers)
                    do! clients.InitializeAsync() |> Async.AwaitTask
                    return Some clients
                }
            else async { return None }

        let! erpcGameId =
            match sharedClientsOpt with
            | None -> async { return None }
            | Some clients ->
                async {
                    let! json = clients.CallToolAsync("new_game", Map.empty) |> Async.AwaitTask
                    let gameId =
                        try (JsonNode.Parse(json) :?> JsonObject).["gameId"].GetValue<string>()
                        with _ -> failwith $"ERPC pre-create game failed: {json}"
                    return Some gameId
                }

        let (p1, p2, p3) = cell.Personas
        let agents =
            [1, p1; 2, p2; 3, p3]
            |> List.map (fun (slot, persona) ->
                let initialMsg = erpcGameId |> Option.map (erpcSlotMessage slot)
                createAgent (makeAgentConfig cell slot persona baseUrl initialMsg) sharedClientsOpt)

        let sw = Stopwatch.StartNew()

        for agent in agents do agent.Post(StartAgent)

        let! gameOver =
            match cell.Variant with
            | ERPC -> waitForAgentsDone agents 600
            | _ -> waitForGameOver logPath 180

        if gameOver then do! Async.Sleep(5000)

        let transcriptList = System.Collections.Generic.List<AgentTranscript>()
        for agent in agents do
            let! t = agent.PostAndAsyncReply(fun r -> StopAgent r)
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
