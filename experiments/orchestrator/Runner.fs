module TicTacToe.Orchestrator.Runner

open System.IO
open System.Net.Http
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics

// ── Persona prompt loading ────────────────────────────────────────────────────

let private loadPersonaPrompt (repoRoot: string) (persona: Persona) (setup: Setup) : string option =
    let personaFile = Path.Combine(repoRoot, "experiments", "personas", $"{Persona.toString persona}.md")
    let md = File.ReadAllText(personaFile)

    match setup with
    | E0 -> None   // E0: no system prompt
    | E1 ->
        // Extract the E1 system prompt block from the markdown
        // The persona files have a "### E1" section with a code block
        let startMarker = "### E1"
        let idx = md.IndexOf(startMarker)
        if idx < 0 then
            // For expert and chaos which have a single prompt block
            let codeStart = md.IndexOf("```\n") + 4
            let codeEnd = md.IndexOf("\n```", codeStart)
            if codeStart > 4 && codeEnd > codeStart then
                Some(md.[codeStart..codeEnd - 1].Trim())
            else Some ""
        else
            let section = md.[idx + startMarker.Length..]
            let codeStart = section.IndexOf("```\n") + 4
            let codeEnd = section.IndexOf("\n```", codeStart)
            if codeStart > 4 && codeEnd > codeStart then
                Some(section.[codeStart..codeEnd - 1].Trim())
            else Some ""
    | ERPC ->
        // E_RPC uses a fixed minimal prompt
        Some "You are playing tic-tac-toe. Use the provided tools to create and play a game to completion."

// ── Single game ───────────────────────────────────────────────────────────────

let private runOneGame
    (config: RunConfig)
    (httpClient: HttpClient)
    (baseUrl: string)
    (systemPrompt: string option)
    (repoRoot: string)
    : Task<GameRecord> =
    task {
        let model = ModelId.toApiString config.Model

        let! (transcript, totalTokens) =
            match config.Setup with
            | ERPC ->
                let prompt = systemPrompt |> Option.defaultValue ""
                RpcAgent.runGame model config.Temperature prompt
            | E0 | E1 ->
                HttpAgent.runGame httpClient model config.Temperature systemPrompt baseUrl

        let metrics = computeMetrics transcript totalTokens
        return { Transcript = transcript; Metrics = metrics }
    }

// ── N-game run ────────────────────────────────────────────────────────────────

let run (config: RunConfig) (repoRoot: string) : Task<RunOutput> =
    task {
        let systemPrompt = loadPersonaPrompt repoRoot config.Persona config.Setup

        // E_RPC doesn't need a live server; HTTP modes do
        let serverHandle =
            if config.Setup = ERPC then None
            else
                Some(ServerProcess.startServer repoRoot config.Commit config.Variant
                     |> Async.AwaitTask |> Async.RunSynchronously)

        use httpClient = new HttpClient()

        try
            let baseUrl = serverHandle |> Option.map (fun h -> h.BaseUrl) |> Option.defaultValue ""
            let games = System.Collections.Generic.List<GameRecord>()

            for _ in 1..config.Games do
                let! record = runOneGame config httpClient baseUrl systemPrompt repoRoot
                games.Add(record)

            let gameList = games |> Seq.toList
            let agg = aggregate gameList

            let cell = {
                Commit = config.Commit
                Variant = Variant.toString config.Variant
                Model = ModelId.toString config.Model
                Persona = Persona.toString config.Persona
                Setup = Setup.toString config.Setup
            }

            return { Cell = cell; Games = gameList; Aggregate = agg }
        finally
            serverHandle |> Option.iter (fun h -> (h :> System.IDisposable).Dispose())
    }
