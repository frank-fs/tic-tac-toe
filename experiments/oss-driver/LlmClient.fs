module TicTacToe.OssDriver.LlmClient

// Lean salvage of the orchestrator's LlmClient: WORKER_* env resolution + 5xx retry,
// but ONLY the text-completion path (no tool-calling apparatus — this driver uses plain
// text actions, which weak OSS models follow more reliably than forced tool-use).

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open TicTacToe.OssDriver.Types

let private http = new HttpClient(Timeout = TimeSpan.FromSeconds 120.0)

[<Literal>]
let private maxAttempts = 3

/// Token accounting for one or more completions. Cost is OpenRouter's reported `usage.cost`
/// (USD) when available, else 0.0 (compute offline from tokens × price).
type Usage = { Prompt: int; Completion: int; Total: int; Cost: float }
let zeroUsage = { Prompt = 0; Completion = 0; Total = 0; Cost = 0.0 }
let addUsage (a: Usage) (b: Usage) =
    { Prompt = a.Prompt + b.Prompt; Completion = a.Completion + b.Completion
      Total = a.Total + b.Total; Cost = a.Cost + b.Cost }

let private parseUsage (resp: JsonObject) : Usage =
    let mutable un = Unchecked.defaultof<JsonNode>
    if resp.TryGetPropertyValue("usage", &un) && un <> null && (un :? JsonObject) then
        let u = un :?> JsonObject
        let gi (k: string) =
            let mutable v = Unchecked.defaultof<JsonNode>
            if u.TryGetPropertyValue(k, &v) && v <> null then (try v.GetValue<int>() with _ -> 0) else 0
        let gf (k: string) =
            let mutable v = Unchecked.defaultof<JsonNode>
            if u.TryGetPropertyValue(k, &v) && v <> null then (try v.GetValue<float>() with _ -> 0.0) else 0.0
        { Prompt = gi "prompt_tokens"; Completion = gi "completion_tokens"; Total = gi "total_tokens"; Cost = gf "cost" }
    else zeroUsage

let private baseUrl backend =
    match backend with
    | Anthropic ->
        Environment.GetEnvironmentVariable "ANTHROPIC_BASE_URL"
        |> Option.ofObj
        |> Option.defaultValue "https://api.anthropic.com"
    | OpenAiCompat ->
        let u =
            (Environment.GetEnvironmentVariable "WORKER_BASE_URL"
             |> Option.ofObj
             |> Option.defaultValue "http://localhost:1234").TrimEnd('/')
        if u.EndsWith "/v1" then u.[.. u.Length - 4].TrimEnd('/') else u

let private apiKey backend =
    match backend with
    | Anthropic ->
        match Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" with
        | null | "" -> failwith "ANTHROPIC_API_KEY not set"
        | k -> k
    | OpenAiCompat ->
        [| "WORKER_API_KEY"; "MOONSHOT_API_KEY" |]
        |> Array.tryPick (fun v ->
            match Environment.GetEnvironmentVariable v with
            | null | "" -> None
            | k -> Some k)
        |> Option.defaultValue "lm-studio"

/// Default model when none passed explicitly.
let defaultModel backend =
    match backend with
    | OpenAiCompat ->
        Environment.GetEnvironmentVariable "WORKER_MODEL"
        |> Option.ofObj
        |> Option.defaultValue "gpt-4o-mini"
    | Anthropic -> "claude-haiku-4-5"

/// OpenRouter provider pin (WORKER_PROVIDER, e.g. "Cerebras"): route a whole sweep to ONE
/// named provider with no fallbacks — same quant/throughput across every game, so provider
/// choice never confounds the factorial. Only applied on the OpenRouter host; no-op when unset.
let private applyProviderPin (backend: Backend) (req: JsonObject) =
    match backend with
    | OpenAiCompat when (baseUrl backend).Contains "openrouter" ->
        match Environment.GetEnvironmentVariable "WORKER_PROVIDER" with
        | null | "" -> ()
        | prov ->
            let order = JsonArray() in order.Add(JsonValue.Create prov)
            let p = JsonObject()
            p["order"] <- order
            p["allow_fallbacks"] <- JsonValue.Create false
            req["provider"] <- p
    | _ -> ()

// Retry transient 5xx (cold/loaded endpoint); 4xx fails immediately.
let private post (backend: Backend) (json: string) : string =
    let url = baseUrl backend + "/v1/chat/completions"
    let rec attempt n =
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey backend) |> ignore
        req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        use resp = http.Send req
        let body = resp.Content.ReadAsStringAsync().Result
        if resp.IsSuccessStatusCode then body
        elif int resp.StatusCode >= 500 && n < maxAttempts then
            System.Threading.Thread.Sleep(500 * n)
            attempt (n + 1)
        else failwithf "LLM API %d (attempt %d): %s" (int resp.StatusCode) n body
    attempt 1

/// One completion with token accounting. messages = (role, content) in order; returns the
/// assistant text and the response's Usage (token counts + OpenRouter cost when reported).
let chatWithUsage (backend: Backend) (model: string) (messages: (string * string) list) : string * Usage =
    let msgs = JsonArray()
    for (role, content) in messages do
        let o = JsonObject()
        o["role"] <- JsonValue.Create role
        o["content"] <- JsonValue.Create content
        msgs.Add o
    let req = JsonObject()
    req["model"] <- JsonValue.Create model
    req["temperature"] <- JsonValue.Create 0.3
    req["max_tokens"] <- JsonValue.Create 1024
    req["messages"] <- msgs
    // OpenRouter returns per-call USD in usage.cost only when asked; harmless elsewhere so
    // guard by host to avoid a stricter OpenAI-compat endpoint 400ing on the unknown field.
    if (baseUrl backend).Contains "openrouter" then
        let u = JsonObject() in u["include"] <- JsonValue.Create true
        req["usage"] <- u
    applyProviderPin backend req
    let respBody = post backend (req.ToJsonString())
    let resp = JsonNode.Parse respBody :?> JsonObject
    let message = resp["choices"].[0].["message"] :?> JsonObject
    let mutable contentNode = Unchecked.defaultof<JsonNode>
    let content =
        if message.TryGetPropertyValue("content", &contentNode) && contentNode <> null
        then contentNode.GetValue<string>() else ""
    content, parseUsage resp

/// One completion. messages = (role, content) in order; returns the assistant text.
let chat (backend: Backend) (model: string) (messages: (string * string) list) : string =
    chatWithUsage backend model messages |> fst

/// Tool-calling completion (for the ERPC arm). messages and tools are raw OpenAI-shaped
/// JsonArrays; returns the assistant message object (detached) — caller inspects content
/// vs tool_calls and appends it + tool results. OpenRouter passes `tools` to the model's
/// native tool-use.
let chatTools (backend: Backend) (model: string) (messages: JsonArray) (tools: JsonArray) : JsonObject =
    let req = JsonObject()
    req["model"] <- JsonValue.Create model
    req["temperature"] <- JsonValue.Create 0.3
    req["max_tokens"] <- JsonValue.Create 1024
    req["messages"] <- messages.DeepClone()
    if tools.Count > 0 then req["tools"] <- tools.DeepClone()
    applyProviderPin backend req
    let respBody = post backend (req.ToJsonString())
    let resp = JsonNode.Parse respBody :?> JsonObject
    (resp["choices"].[0].["message"]).DeepClone() :?> JsonObject
