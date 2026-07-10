module TicTacToe.OssDriver.ErpcDriver

// ERPC (RPC/MCP null-hypothesis) arm of the ONE driver. A tool-calling ReAct loop: the model
// calls the mcp-rpc tools (their rich schemas ARE the discovery surface); we execute via the
// MCP client and feed results back. MOMENT 1/2 reports are plain-text assistant content (same
// grading as the HTTP arm). Identity is the model's: authenticate once, reuse the token.
// NOTE: single-seat (one server subprocess per run) — fine for discovery; 2-player ERPC games
// need one shared connection multiplexing identities (a later step).

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open ModelContextProtocol.Client

let private erpcColdPath = "experiments/oss-driver/erpc-coldstart-prompt.md"
let private erpcDirectedPath = "experiments/oss-driver/erpc-directed-prompt.md"
let private factsPath =
    Environment.GetEnvironmentVariable "DIRECTED_FACTS_PATH"
    |> Option.ofObj |> Option.defaultValue "experiments/oss-driver/directed-facts.json"
let private defaultDll = "experiments/mcp-rpc/bin/Debug/net10.0/TicTacToe.McpRpc.dll"

let private msg (role: string) (content: string) =
    let o = JsonObject()
    o.["role"] <- JsonValue.Create role
    o.["content"] <- JsonValue.Create content
    o :> JsonNode

/// MCP tools -> OpenAI tool definitions (the schemas the model sees = the RPC discovery surface).
let private toolDefs (tools: IList<McpClientTool>) : JsonArray =
    let arr = JsonArray()
    for t in tools do
        let f = JsonObject()
        f.["name"] <- JsonValue.Create t.Name
        f.["description"] <- JsonValue.Create t.Description
        f.["parameters"] <- JsonNode.Parse(t.JsonSchema.GetRawText())
        let td = JsonObject()
        td.["type"] <- JsonValue.Create "function"
        td.["function"] <- f
        arr.Add td
    arr

/// The sole bootstrap affordance for the FAIR ERPC arm: a discovery meta-tool. Its presence is the RPC
/// channel floor (analogous to "GET to read / POST to act" for HTTP) — the CONTENT (the real tools and
/// their schemas) is still discovered by CALLING it, never pre-injected. This de-injection makes ERPC a
/// fair null hypothesis: both arms must reach for their contract (HTTP fetches /profile; ERPC calls
/// list_tools), rather than ERPC being handed its schema for free (the issue #5 embed cheat, mirror-imaged).
let private listToolsDefs () : JsonArray =
    let f = JsonObject()
    f.["name"] <- JsonValue.Create "list_tools"
    f.["description"] <- JsonValue.Create "Discover the tools available here: returns their names and input schemas. Call this first — nothing else is available until you do."
    f.["parameters"] <- JsonNode.Parse("""{"type":"object","properties":{}}""")
    let td = JsonObject()
    td.["type"] <- JsonValue.Create "function"
    td.["function"] <- f
    let arr = JsonArray()
    arr.Add td
    arr

let private parseArgs (s: string) : IReadOnlyDictionary<string, obj> =
    let d = Dictionary<string, obj>()
    (try
        match JsonNode.Parse s with
        | :? JsonObject as o ->
            for kv in o do
                d.[kv.Key] <- (try box (kv.Value.GetValue<string>()) with _ -> box (kv.Value.ToJsonString()))
        | _ -> ()
     with _ -> ())
    d :> IReadOnlyDictionary<_, _>

let private directedPrompt (cfg: Driver.Config) : string =
    let facts = JsonNode.Parse(IO.File.ReadAllText factsPath) :?> JsonObject
    let f (k: string) = facts.[k].GetValue<string>()
    (Driver.loadPrompt erpcDirectedPath).Replace("{ROLE}", cfg.Role).Replace("{APPIS}", f "appIs").Replace("{GOAL}", f "goal")

let run (cfg: Driver.Config) : string =
    let dll = Environment.GetEnvironmentVariable "ERPC_SERVER_DLL" |> Option.ofObj |> Option.defaultValue defaultDll
    let logPath = Environment.GetEnvironmentVariable "ERPC_LOG_PATH" |> Option.ofObj |> Option.defaultValue "/tmp/erpc-game.jsonl"
    IO.File.WriteAllText(logPath, "")  // fresh wire log per run (the server appends event_type lines here)
    use erpc = new McpClient.Erpc(dll, IO.Directory.GetCurrentDirectory(), readOnlyDict [ "TICTACTOE_REQUEST_LOG_PATH", logPath ])
    // De-injected: real schemas are NOT pre-loaded. Model starts with list_tools only; calling it unlocks
    // the real tools (discovered <- true) and returns their schemas as the tool result. Directed arm keeps
    // the tools pre-loaded (it is the play-skill control, not a discovery test).
    let realDefs = toolDefs erpc.Tools
    let listOnly = listToolsDefs ()
    let mutable discovered = not cfg.ColdStart
    let sys = if cfg.ColdStart then Driver.loadPrompt erpcColdPath else directedPrompt cfg
    let messages = JsonArray()
    messages.Add(msg "system" sys)
    messages.Add(msg "user" "Begin.")
    let transcript = ResizeArray<string * string>()
    transcript.Add("system", sys)
    let debug = not (isNull (Environment.GetEnvironmentVariable "DRIVER_DEBUG"))
    let mutable step, moves = 0, 0
    let mutable outcome = "incomplete"
    let mutable stop = false
    while not stop && step < cfg.MaxTurns do
        let assistant =
            try LlmClient.chatTools cfg.Backend cfg.Model messages (if discovered then realDefs else listOnly)
            with e ->
                let o = JsonObject() in o.["content"] <- JsonValue.Create(sprintf "<chat error: %s>" e.Message)
                o
        messages.Add(assistant.DeepClone())
        let content = match assistant.["content"] with | null -> "" | c -> (try c.GetValue<string>() with _ -> "")
        if content <> "" then transcript.Add("assistant", content)
        match assistant.["tool_calls"] with
        | :? JsonArray as calls when calls.Count > 0 ->
            for call in calls do
                let callObj = call :?> JsonObject
                let id = callObj.["id"].GetValue<string>()
                let fn = callObj.["function"] :?> JsonObject
                let name = fn.["name"].GetValue<string>()
                let argsJson = match fn.["arguments"] with | null -> "{}" | a -> a.GetValue<string>()
                let result =
                    if name = "list_tools" then
                        discovered <- true                       // discovery unlocks the real tools next turn
                        realDefs.ToJsonString()                  // the contract, delivered on request
                    elif not discovered then
                        """{"error":"unknown tool — call list_tools first to discover the available tools"}"""
                    else try erpc.Call name (parseArgs argsJson) with e -> sprintf "{\"error\":\"%s\"}" e.Message
                if debug then eprintfn "[%s] TOOL %s %s -> %s" cfg.Role name argsJson (result.Substring(0, min 140 result.Length))
                if name = "make_move" && not (result.Contains "\"error\"") then moves <- moves + 1
                if result.Contains "\"status\":\"won\"" || result.Contains "\"status\":\"draw\"" then outcome <- "over"; stop <- true
                let tr = JsonObject()
                tr.["role"] <- JsonValue.Create "tool"
                tr.["tool_call_id"] <- JsonValue.Create id
                tr.["content"] <- JsonValue.Create result
                messages.Add tr
                transcript.Add("tool", result)
            if moves >= cfg.MaxMoves then outcome <- "move_cap"; stop <- true
        | _ ->
            messages.Add(msg "user" "Continue: call a tool to act, or emit your MOMENT report as plain text.")
        step <- step + 1

    if not stop && outcome = "incomplete" then outcome <- "window_truncated"

    match Environment.GetEnvironmentVariable "TRANSCRIPT_PATH" with
    | null | "" -> ()
    | path ->
        use w = new IO.StreamWriter(path)
        for (role, content) in transcript do
            let o = JsonObject()
            o.["role"] <- JsonValue.Create role
            o.["content"] <- JsonValue.Create content
            w.WriteLine(o.ToJsonString())

    let res = JsonObject()
    res.["role"] <- JsonValue.Create cfg.Role
    res.["model"] <- JsonValue.Create cfg.Model
    res.["arm"] <- JsonValue.Create "erpc"
    res.["actions"] <- JsonValue.Create step
    res.["attempts"] <- JsonValue.Create moves
    res.["turns"] <- JsonValue.Create step
    res.["moves_submitted"] <- JsonValue.Create moves
    res.["outcome"] <- JsonValue.Create outcome
    res.ToJsonString()
