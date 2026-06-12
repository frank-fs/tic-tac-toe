module TicTacToe.Orchestrator.Orchestrator

open System
open System.Diagnostics
open System.IO
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics
open TicTacToe.Orchestrator.Persistence
open TicTacToe.Orchestrator.ServerLogTail
open TicTacToe.Orchestrator.ServerProcess
open TicTacToe.Orchestrator.Agent

let private makeAgentConfig (cell: CellSpec) (slot: int) (persona: Persona) (baseUrl: string) : AgentConfig =
    { Id = $"agent-{slot}"
      Persona = persona
      Model = cell.Model
      BaseUrl = baseUrl
      McpServers = cell.McpServers
      MaxTurns = cell.MaxTurnsPerAgent
      Temperature = cell.Temperature }

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

        let (p1, p2, p3) = cell.Personas
        let agents =
            [1, p1; 2, p2; 3, p3]
            |> List.map (fun (slot, persona) ->
                createAgent (makeAgentConfig cell slot persona baseUrl))

        let sw = Stopwatch.StartNew()

        for agent in agents do agent.Post(StartAgent)

        let! gameOver = waitForGameOver logPath 180

        if gameOver then do! Async.Sleep(5000)

        let transcriptList = System.Collections.Generic.List<AgentTranscript>()
        for agent in agents do
            let! t = agent.PostAndAsyncReply(fun r -> StopAgent r)
            transcriptList.Add(t)

        sw.Stop()
        let transcripts = transcriptList |> Seq.map (fun t -> t.AgentId, t) |> Map.ofSeq

        let events = (startTail logPath).GetEvents()

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
