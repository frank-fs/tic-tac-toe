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

/// One completion. messages = (role, content) in order; returns the assistant text.
let chat (backend: Backend) (model: string) (messages: (string * string) list) : string =
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
    let respBody = post backend (req.ToJsonString())
    let resp = JsonNode.Parse respBody :?> JsonObject
    let message = resp["choices"].[0].["message"] :?> JsonObject
    let mutable contentNode = Unchecked.defaultof<JsonNode>
    if message.TryGetPropertyValue("content", &contentNode) && contentNode <> null then
        contentNode.GetValue<string>()
    else
        ""
