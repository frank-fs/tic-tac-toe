module TicTacToe.Orchestrator.LlmClient

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types

let private httpClient = new HttpClient()

// ── Backend-specific connection config ────────────────────────────────────────
// Env var names match the ask-kimi script convention:
//   WORKER_BASE_URL   — OpenAI-compat endpoint (LM Studio, Moonshot, etc.)
//   WORKER_API_KEY    — API key for that endpoint (fallback: MOONSHOT_API_KEY)
//   WORKER_MODEL      — default model name for OpenAI-compat backend
// Anthropic backend uses its own standard vars:
//   ANTHROPIC_BASE_URL — override for Anthropic-compat gateway (e.g. LM Studio Anthropic endpoint)
//   ANTHROPIC_API_KEY  — API key for Anthropic

let private resolveBaseUrl (backend: Backend) =
    match backend with
    | Anthropic ->
        Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
        |> Option.ofObj
        |> Option.defaultValue "https://api.anthropic.com"
    | OpenAiCompat ->
        let url =
            (Environment.GetEnvironmentVariable("WORKER_BASE_URL")
             |> Option.ofObj
             |> Option.defaultValue "http://localhost:1234").TrimEnd('/')
        if url.EndsWith("/v1") then url.[..url.Length - 4] else url

let private resolveApiKey (backend: Backend) =
    match backend with
    | Anthropic ->
        match Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") with
        | null | "" -> failwith "ANTHROPIC_API_KEY environment variable not set"
        | k -> k
    | OpenAiCompat ->
        // Mirror ask-kimi: WORKER_API_KEY, fall back to MOONSHOT_API_KEY, then "lm-studio"
        [| "WORKER_API_KEY"; "MOONSHOT_API_KEY" |]
        |> Array.tryPick (fun v ->
            match Environment.GetEnvironmentVariable(v) with
            | null | "" -> None
            | k -> Some k)
        |> Option.defaultValue "lm-studio"

// True only when hitting the real Anthropic API (not a local gateway).
let private isRealAnthropic (backend: Backend) =
    backend = Anthropic
    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"))

// ── Public types ───────────────────────────────────────────────────────────────

type ToolDef = {
    Name: string
    Description: string
    /// JSON schema object — same shape for both backends (becomes input_schema / parameters).
    InputSchema: JsonNode
}

type ToolCall = {
    Id: string
    Name: string
    Input: JsonObject
}

type TurnResult =
    | ToolCalls of calls: ToolCall list * inputTokens: int * outputTokens: int
    | Done of text: string * inputTokens: int * outputTokens: int

// ── Request builders ───────────────────────────────────────────────────────────

let private buildAnthropicRequest
    (model: string) (temperature: float) (systemPrompt: string option)
    (tools: ToolDef list) (forceToolUse: bool) (messages: JsonArray) : JsonObject =

    let req = JsonObject()
    req["model"] <- JsonValue.Create(model)
    req["max_tokens"] <- JsonValue.Create(4096)
    req["temperature"] <- JsonValue.Create(temperature)

    match systemPrompt with
    | Some prompt ->
        let sysArr = JsonArray()
        let sysBlock = JsonObject()
        sysBlock["type"] <- JsonValue.Create("text")
        sysBlock["text"] <- JsonValue.Create(prompt)
        sysArr.Add(sysBlock)
        req["system"] <- sysArr
    | None -> ()

    if not tools.IsEmpty then
        let toolArr = JsonArray()
        for t in tools do
            let td = JsonObject()
            td["name"] <- JsonValue.Create(t.Name)
            td["description"] <- JsonValue.Create(t.Description)
            td["input_schema"] <- t.InputSchema.DeepClone()
            toolArr.Add(td)
        req["tools"] <- toolArr
        if forceToolUse then
            let tc = JsonObject()
            tc["type"] <- JsonValue.Create("any")
            req["tool_choice"] <- tc

    req["messages"] <- messages.DeepClone() :?> JsonArray
    req

let private buildOpenAiRequest
    (model: string) (temperature: float) (systemPrompt: string option)
    (tools: ToolDef list) (forceToolUse: bool) (messages: JsonArray) : JsonObject =

    let req = JsonObject()
    req["model"] <- JsonValue.Create(model)
    req["max_tokens"] <- JsonValue.Create(4096)
    req["temperature"] <- JsonValue.Create(temperature)

    // OpenAI puts system prompt as first message in the array
    let msgs = JsonArray()
    match systemPrompt with
    | Some prompt ->
        let sys = JsonObject()
        sys["role"] <- JsonValue.Create("system")
        sys["content"] <- JsonValue.Create(prompt)
        msgs.Add(sys)
    | None -> ()
    for m in messages do
        msgs.Add(m.DeepClone())
    req["messages"] <- msgs

    if not tools.IsEmpty then
        let toolArr = JsonArray()
        for t in tools do
            let fn = JsonObject()
            fn["name"] <- JsonValue.Create(t.Name)
            fn["description"] <- JsonValue.Create(t.Description)
            fn["parameters"] <- t.InputSchema.DeepClone()
            let td = JsonObject()
            td["type"] <- JsonValue.Create("function")
            td["function"] <- fn
            toolArr.Add(td)
        req["tools"] <- toolArr
        if forceToolUse then
            req["tool_choice"] <- JsonValue.Create("required")

    req

// ── HTTP dispatch ──────────────────────────────────────────────────────────────

let private postMessages (backend: Backend) (req: JsonObject) : Task<JsonObject> =
    task {
        let baseUrl = resolveBaseUrl backend
        let apiKey = resolveApiKey backend
        let path =
            match backend with
            | Anthropic -> "/v1/messages"
            | OpenAiCompat -> "/v1/chat/completions"

        use request = new HttpRequestMessage(HttpMethod.Post, Uri($"{baseUrl}{path}"))

        match backend with
        | Anthropic ->
            request.Headers.Add("x-api-key", apiKey)
            request.Headers.Add("anthropic-version", "2023-06-01")
            // Only send prompt-caching beta header to the real Anthropic API
            if isRealAnthropic backend then
                request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31")
        | OpenAiCompat ->
            request.Headers.Add("Authorization", $"Bearer {apiKey}")

        request.Content <- JsonContent.Create(req)
        let! response = httpClient.SendAsync(request)
        let! json = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            failwithf "LLM API error %d: %s" (int response.StatusCode) json
        return JsonNode.Parse(json) :?> JsonObject
    }

// ── Response parsers ───────────────────────────────────────────────────────────

let private parseAnthropicResponse (resp: JsonObject) : TurnResult =
    let usage = resp["usage"] :?> JsonObject
    let inputTokens = usage["input_tokens"].GetValue<int>()
    let outputTokens = usage["output_tokens"].GetValue<int>()

    let content = resp["content"] :?> JsonArray
    let toolUses =
        content
        |> Seq.cast<JsonNode>
        |> Seq.filter (fun n -> ((n :?> JsonObject)["type"]).GetValue<string>() = "tool_use")
        |> Seq.map (fun n ->
            let o = n :?> JsonObject
            { Id = o["id"].GetValue<string>()
              Name = o["name"].GetValue<string>()
              Input = o["input"] :?> JsonObject })
        |> Seq.toList

    if toolUses.IsEmpty then
        let text =
            content
            |> Seq.cast<JsonNode>
            |> Seq.tryFind (fun n -> ((n :?> JsonObject)["type"]).GetValue<string>() = "text")
            |> Option.map (fun n -> ((n :?> JsonObject)["text"]).GetValue<string>())
            |> Option.defaultValue ""
        Done(text, inputTokens, outputTokens)
    else
        ToolCalls(toolUses, inputTokens, outputTokens)

let private parseOpenAiResponse (resp: JsonObject) : TurnResult =
    let usage = resp["usage"] :?> JsonObject
    let inputTokens = usage["prompt_tokens"].GetValue<int>()
    let outputTokens = usage["completion_tokens"].GetValue<int>()

    let message = resp["choices"].[0].["message"] :?> JsonObject
    let mutable toolCallsNode: JsonNode = null
    let hasToolCalls = message.TryGetPropertyValue("tool_calls", &toolCallsNode)

    if not hasToolCalls || toolCallsNode = null then
        let mutable contentNode: JsonNode = null
        let text =
            if message.TryGetPropertyValue("content", &contentNode) && contentNode <> null then
                contentNode.GetValue<string>()
            else ""
        Done(text, inputTokens, outputTokens)
    else
        let calls =
            (toolCallsNode :?> JsonArray)
            |> Seq.cast<JsonNode>
            |> Seq.map (fun n ->
                let o = n :?> JsonObject
                let fn = o["function"] :?> JsonObject
                let argsStr = fn["arguments"].GetValue<string>()
                let input =
                    try JsonNode.Parse(argsStr) :?> JsonObject
                    with _ -> JsonObject()
                { Id = o["id"].GetValue<string>()
                  Name = fn["name"].GetValue<string>()
                  Input = input })
            |> Seq.toList
        if calls.IsEmpty then Done("", inputTokens, outputTokens)
        else ToolCalls(calls, inputTokens, outputTokens)

// ── Core turn ──────────────────────────────────────────────────────────────────

let runTurn
    (backend: Backend)
    (model: string) (temperature: float) (systemPrompt: string option)
    (tools: ToolDef list) (forceToolUse: bool) (messages: JsonArray)
    : Task<TurnResult> =
    task {
        let req =
            match backend with
            | Anthropic -> buildAnthropicRequest model temperature systemPrompt tools forceToolUse messages
            | OpenAiCompat -> buildOpenAiRequest model temperature systemPrompt tools forceToolUse messages
        let! resp = postMessages backend req
        return
            match backend with
            | Anthropic -> parseAnthropicResponse resp
            | OpenAiCompat -> parseOpenAiResponse resp
    }

// ── Message history helpers ────────────────────────────────────────────────────

let appendUserText (messages: JsonArray) (text: string) : JsonArray =
    let msg = JsonObject()
    msg["role"] <- JsonValue.Create("user")
    msg["content"] <- JsonValue.Create(text)
    messages.Add(msg.DeepClone())
    messages

/// Append assistant turn with tool calls. Format differs per backend.
let appendAssistantToolUse (backend: Backend) (messages: JsonArray) (calls: ToolCall list) : JsonArray =
    match backend with
    | Anthropic ->
        let msg = JsonObject()
        msg["role"] <- JsonValue.Create("assistant")
        let content = JsonArray()
        for call in calls do
            let block = JsonObject()
            block["type"] <- JsonValue.Create("tool_use")
            block["id"] <- JsonValue.Create(call.Id)
            block["name"] <- JsonValue.Create(call.Name)
            block["input"] <- call.Input.DeepClone()
            content.Add(block)
        msg["content"] <- content
        messages.Add(msg.DeepClone())
    | OpenAiCompat ->
        let msg = JsonObject()
        msg["role"] <- JsonValue.Create("assistant")
        msg["content"] <- JsonValue.Create(null: string)
        let toolCalls = JsonArray()
        for call in calls do
            let fn = JsonObject()
            fn["name"] <- JsonValue.Create(call.Name)
            fn["arguments"] <- JsonValue.Create(call.Input.ToJsonString())
            let tc = JsonObject()
            tc["id"] <- JsonValue.Create(call.Id)
            tc["type"] <- JsonValue.Create("function")
            tc["function"] <- fn
            toolCalls.Add(tc)
        msg["tool_calls"] <- toolCalls
        messages.Add(msg.DeepClone())
    messages

/// Append tool results. Anthropic: one user message with array of tool_result blocks.
/// OpenAI: one role:tool message per result (separate message for each).
let appendToolResults (backend: Backend) (messages: JsonArray) (results: (string * string) list) : JsonArray =
    match backend with
    | Anthropic ->
        let msg = JsonObject()
        msg["role"] <- JsonValue.Create("user")
        let content = JsonArray()
        for (id, result) in results do
            let block = JsonObject()
            block["type"] <- JsonValue.Create("tool_result")
            block["tool_use_id"] <- JsonValue.Create(id)
            block["content"] <- JsonValue.Create(result)
            content.Add(block)
        msg["content"] <- content
        messages.Add(msg.DeepClone())
    | OpenAiCompat ->
        for (id, result) in results do
            let msg = JsonObject()
            msg["role"] <- JsonValue.Create("tool")
            msg["tool_call_id"] <- JsonValue.Create(id)
            msg["content"] <- JsonValue.Create(result)
            messages.Add(msg.DeepClone())
    messages
