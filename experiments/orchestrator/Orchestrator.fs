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

// Shared mental model. An LLM treats one read as permanently valid; this names the gap by
// framing the game as two-player request/response where REFRESH (re-requesting state) is a
// REQUIRED skill, not an optional check. Surface-bound below: REFRESH = navigate+snapshot
// (browser) or get_state (RPC). Moves are NOT scripted — the agent still chooses where to play.
let private concurrencyModel =
    "This is a live two-player game played over request/response: you and your opponent each " +
    "act against one shared game state on the server. Every response you receive is only a " +
    "snapshot of that state at that instant — the moment your opponent acts, your snapshot is " +
    "stale. Nothing is pushed to you; the only way to observe your opponent's move is to request " +
    "the state again. Treat REFRESH — re-requesting the current state — as a required step before " +
    "every move, never an optional check. "

// Browser binding of the per-turn protocol; mirrors the RPC arm step-for-step. REFRESH = navigate
// to the game's page + snapshot. The no-JS page does not self-update, so the re-fetch is the navigate.
let private browserHint =
    "On this web app, REFRESH means: navigate to the game's page (the page showing the board — " +
    "from a list, click into the game), then take a snapshot to read its accessibility tree (the " +
    "controls, their labels and states). Each turn: (1) REFRESH — this is the only way to see new " +
    "moves, yours or your opponent's. (2) Immediately before you move, REFRESH once more so you act " +
    "on the current board; a stale snapshot submits stale data and is rejected. (3) Click your move " +
    "by the ref id (e1, e2, …) from the LATEST snapshot, not a ref you saw earlier — a move only " +
    "applies on your turn. (4) After moving, REFRESH to confirm it was accepted; the turn then passes " +
    "to your opponent. (5) Do not move again right away — keep REFRESHing until the board shows it is " +
    "YOUR turn; acting off-turn is an error. (6) A rejection, an error, or 'not your turn' means your " +
    "view was stale, never that you should stop: REFRESH, wait for your turn, then retry. Keep going " +
    "until the game is actually over (a win or a draw). If your tools expose the network log, you can " +
    "also inspect the HTTP status (2xx accepted, 4xx rejected)."

let private slotMessage (surface: AgentSurface) (baseUrl: string) : string =
    match surface with
    | Rpc ->
        concurrencyModel +
        "On this tool server, REFRESH means: call get_state to read the current board. Call " +
        "list_games to find the game; the server knows which side you are (X or O). Each turn: " +
        "(1) REFRESH — this is the only way to see new moves, yours or your opponent's. " +
        "(2) Immediately before you move, REFRESH once more so you act on the current state. " +
        "(3) Call make_move with your position — a move only succeeds on your turn. (4) After moving, " +
        "REFRESH to confirm it was accepted; the turn then passes to your opponent. (5) Do not move " +
        "again right away — keep REFRESHing until it is YOUR turn; acting off-turn is an error. " +
        "(6) A rejection, an error, or 'not your turn' means your view was stale, never that you " +
        "should stop: REFRESH, wait for your turn, then retry. Keep going until the game is actually " +
        "over (a win or a draw)."
    | Browser ->
        $"The game is a web app at {baseUrl}. {concurrencyModel}{browserHint}"

// Browser cells: stop waiting on the FIRST of — a GameOver in the server log (a real
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
                    // ERPC writes its event log to a FILE (never stdout) via this env var,
                    // so per-role metrics can be computed the same way as the browser arms.
                    let erpcServers =
                        cell.McpServers
                        |> List.map (fun s -> { s with Env = Array.append s.Env [| "TICTACTOE_REQUEST_LOG_PATH", logPath |] })
                    let clients = new McpClientSet(erpcServers)
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
                let initialMsg = slotMessage (surfaceOf cell.McpServers) baseUrl
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

        let totalTokens =
            transcriptList
            |> Seq.collect (fun t -> t.LlmTurns)
            |> Seq.sumBy (fun turn -> turn.InputTokens + turn.OutputTokens)
        let roles = resolveRoles events

        let durationSeconds = sw.Elapsed.TotalSeconds
        let metrics = computeCellMetrics cell.Id (transcriptList |> Seq.toList) roles events durationSeconds cellStart totalTokens

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

// A spawned MCP server may carry a relative `--project <path>`. The child process inherits the
// orchestrator's cwd, so a relative path breaks whenever the orchestrator is launched from
// anywhere but the repo root (observed: "server shut down unexpectedly" — the project path did
// not resolve). Absolutize against repoRoot so the spawn is cwd-independent.
let private absolutizeMcpPaths (repoRoot: string) (cells: CellSpec list) : CellSpec list =
    let fixArgs (args: string[]) =
        match Array.tryFindIndex ((=) "--project") args with
        | Some i when i + 1 < args.Length && not (Path.IsPathRooted args.[i + 1]) ->
            let copy = Array.copy args
            copy.[i + 1] <- Path.GetFullPath(Path.Combine(repoRoot, args.[i + 1]))
            copy
        | _ -> args
    let fixCfg (cfg: McpServerConfig) =
        if cfg.Command = "dotnet" then { cfg with Arguments = fixArgs cfg.Arguments } else cfg
    cells |> List.map (fun c -> { c with McpServers = c.McpServers |> List.map fixCfg })

// Pre-build any dotnet-based MCP server project so the cell can spawn it with --no-build. A bare
// `dotnet run` builds at connect time: build output on stdout corrupts the stdio MCP handshake,
// and a build concurrent with the orchestrator's own contends on shared obj/. Build once, loud.
let private prebuildMcpServers (cells: CellSpec list) : unit =
    let projectOf (cfg: McpServerConfig) =
        if cfg.Command = "dotnet" && Array.contains "run" cfg.Arguments then
            Array.tryFindIndex ((=) "--project") cfg.Arguments
            |> Option.bind (fun i -> Array.tryItem (i + 1) cfg.Arguments)
        else None
    cells
    |> List.collect (fun c -> c.McpServers)
    |> List.choose projectOf
    |> List.distinct
    |> List.iter (fun proj ->
        printfn $"[matrix] pre-building MCP server: {proj}"
        let psi = ProcessStartInfo("dotnet", $"build \"{proj}\" --nologo -v q", UseShellExecute = false)
        use p = Process.Start(psi)
        p.WaitForExit()
        if p.ExitCode <> 0 then failwithf "pre-build of MCP server %s failed (exit %d)" proj p.ExitCode)

let runMatrix (repoRoot: string) (matrixName: string) (cells: CellSpec list) : Async<unit> =
    async {
        let cells = absolutizeMcpPaths repoRoot cells
        let stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
        archivePriorRun repoRoot matrixName (cells |> List.map (fun c -> c.Id)) stamp
        prebuildMcpServers cells
        printfn $"[matrix] starting: {matrixName} ({cells.Length} cells)"
        let results = System.Collections.Generic.List<CellResult>()

        // Per-cell isolation: one cell's failure (e.g. a flaky MCP connect) must not abandon the
        // rest of the matrix. Log and continue. (Server dispose on a mid-cell throw is not yet
        // covered — a failed cell may leak its web server until process exit; acceptable for now.)
        for cell in cells do
            try
                let! result = runCell repoRoot cell
                results.Add(result)
            with ex ->
                printfn $"[cell] FAILED: {cell.Id} — {ex.Message} (continuing)"

        saveManifest repoRoot matrixName cells (results |> Seq.toList)
        printfn $"[matrix] complete: {matrixName}"
    }
