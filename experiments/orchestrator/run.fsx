#!/usr/bin/env dotnet fsi

/// Agent Persona Orchestrator
/// Runs HTTP interactions against the tic-tac-toe server using configured personas.
/// Results are captured as JSON transcripts in experiments/results/.

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization

// ---------- Configuration ----------

[<CLIMutable>]
type Config =
    { serverUrl: string
      defaultGameCount: int
      timeoutSeconds: int
      maxRequestsPerGame: int }

let loadConfig (path: string) =
    let json = File.ReadAllText(path)
    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    JsonSerializer.Deserialize<Config>(json, options)

// ---------- Transcript Types ----------

type RequestRecord =
    { Timestamp: DateTimeOffset
      Method: string
      Url: string
      RequestHeaders: Map<string, string>
      RequestBody: string option
      StatusCode: int
      ResponseHeaders: Map<string, string>
      ResponseBody: string
      DurationMs: int64 }

type GameTranscript =
    { Persona: string
      Instance: string
      GameIndex: int
      StartedAt: DateTimeOffset
      Requests: RequestRecord list
      Outcome: string }

type RunResult =
    { Persona: string
      Instance: string
      Config: Config
      Games: GameTranscript list
      Summary: Map<string, obj> }

// ---------- HTTP Client ----------

let createClient (config: Config) (agentId: string) =
    let handler = new HttpClientHandler()
    let client = new HttpClient(handler)
    client.BaseAddress <- Uri(config.serverUrl)
    client.Timeout <- TimeSpan.FromSeconds(float config.timeoutSeconds)
    client.DefaultRequestHeaders.Add("X-Agent-Id", agentId)
    client

let executeRequest (client: HttpClient) (method: HttpMethod) (url: string) (body: HttpContent option) = async {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let request = new HttpRequestMessage(method, url)

    match body with
    | Some content -> request.Content <- content
    | None -> ()

    let! response =
        client.SendAsync(request)
        |> Async.AwaitTask

    sw.Stop()

    let! responseBody =
        response.Content.ReadAsStringAsync()
        |> Async.AwaitTask

    let requestHeaders =
        request.Headers
        |> Seq.collect (fun kvp -> kvp.Value |> Seq.map (fun v -> kvp.Key, v))
        |> Seq.map (fun (k, v) -> k, v)
        |> Map.ofSeq

    let responseHeaders =
        response.Headers
        |> Seq.collect (fun kvp -> kvp.Value |> Seq.map (fun v -> kvp.Key, v))
        |> Seq.map (fun (k, v) -> k, v)
        |> Map.ofSeq

    let requestBody =
        match body with
        | Some content ->
            content.ReadAsStringAsync()
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Some
        | None -> None

    return
        { Timestamp = DateTimeOffset.UtcNow
          Method = method.Method
          Url = url
          RequestHeaders = requestHeaders
          RequestBody = requestBody
          StatusCode = int response.StatusCode
          ResponseHeaders = responseHeaders
          ResponseBody = responseBody
          DurationMs = sw.ElapsedMilliseconds }
}

// ---------- Discovery ----------

let discover (client: HttpClient) (url: string) = async {
    let! record = executeRequest client HttpMethod.Options url None
    return record
}

// ---------- Transcript Persistence ----------

let saveResult (resultsDir: string) (result: RunResult) =
    Directory.CreateDirectory(resultsDir) |> ignore

    let timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
    let fileName = $"{result.Persona}-{result.Instance}-{timestamp}.json"
    let path = Path.Combine(resultsDir, fileName)

    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        )

    let json = JsonSerializer.Serialize(result, options)
    File.WriteAllText(path, json)
    printfn $"Results saved to: {path}"
    path

// ---------- Main Entry Point ----------

let run (persona: string) (instance: string) (configPath: string) (resultsDir: string) =
    let config = loadConfig configPath
    let agentId = $"agent-{persona}-{instance}"
    let client = createClient config agentId

    printfn $"Orchestrator starting"
    printfn $"  Persona:  {persona}"
    printfn $"  Instance: {instance}"
    printfn $"  Agent ID: {agentId}"
    printfn $"  Server:   {config.serverUrl}"
    printfn $"  Games:    {config.defaultGameCount}"
    printfn ""

    // Phase 1: Discovery
    printfn "Phase 1: Discovery"
    let discoveryRecord =
        discover client "/"
        |> Async.RunSynchronously

    printfn $"  OPTIONS / -> {discoveryRecord.StatusCode}"
    printfn $"  Response headers: {discoveryRecord.ResponseHeaders |> Map.count} headers"
    printfn ""

    // Phase 2: Game loop (placeholder - will be driven by Claude API in #22/#23)
    printfn "Phase 2: Game loop (framework only - actual play logic to be added in #22/#23)"
    let games =
        [ for i in 1 .. config.defaultGameCount do
            { Persona = persona
              Instance = instance
              GameIndex = i
              StartedAt = DateTimeOffset.UtcNow
              Requests = [ discoveryRecord ]
              Outcome = "not-started" } ]

    // Phase 3: Save results
    let result =
        { Persona = persona
          Instance = instance
          Config = config
          Games = games
          Summary =
            Map.ofList
                [ "totalRequests", box 1
                  "gamesPlayed", box 0
                  "gamesCompleted", box 0
                  "status", box "framework-only" ] }

    let resultPath = saveResult resultsDir result
    printfn ""
    printfn $"Run complete. Results: {resultPath}"
    result

// ---------- CLI ----------

let scriptDir = __SOURCE_DIRECTORY__
let defaultConfigPath = Path.Combine(scriptDir, "config.json")
let defaultResultsDir = Path.Combine(scriptDir, "..", "results")

let args = fsi.CommandLineArgs |> Array.toList

match args with
| _ :: persona :: [] ->
    let instance = Guid.NewGuid().ToString("N").[..7]
    run persona instance defaultConfigPath defaultResultsDir |> ignore
| _ :: persona :: instance :: [] ->
    run persona instance defaultConfigPath defaultResultsDir |> ignore
| _ :: persona :: instance :: configPath :: [] ->
    run persona instance configPath defaultResultsDir |> ignore
| _ :: persona :: instance :: configPath :: resultsDir :: [] ->
    run persona instance configPath resultsDir |> ignore
| _ ->
    printfn "Usage: dotnet fsi run.fsx <persona> [instance] [config.json] [results-dir]"
    printfn ""
    printfn "Personas: beginner, expert, chaos"
    printfn ""
    printfn "Examples:"
    printfn "  dotnet fsi run.fsx beginner"
    printfn "  dotnet fsi run.fsx expert test-001"
    printfn "  dotnet fsi run.fsx chaos chaos-1 ./config.json ../results"
