module TicTacToe.Orchestrator.AnthropicClient

open System
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

let private httpClient = new HttpClient(BaseAddress = Uri("https://api.anthropic.com"))

let private apiKey () =
    match Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") with
    | null | "" -> failwith "ANTHROPIC_API_KEY environment variable not set"
    | k -> k

// ── Tool definition ───────────────────────────────────────────────────────────

type ToolDef = {
    Name: string
    Description: string
    InputSchema: JsonNode
}

// ── Turn result ───────────────────────────────────────────────────────────────

type ToolCall = {
    Id: string
    Name: string
    Input: JsonObject
}

type TurnResult =
    | ToolCalls of calls: ToolCall list * inputTokens: int * outputTokens: int
    | Done of text: string * inputTokens: int * outputTokens: int

// ── Request builder ───────────────────────────────────────────────────────────

let private buildRequest (model: string) (temperature: float) (systemPrompt: string option) (tools: ToolDef list) (messages: JsonArray) : JsonObject =
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
        let cc = JsonObject()
        cc["type"] <- JsonValue.Create("ephemeral")
        sysBlock["cache_control"] <- cc
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

    req["messages"] <- messages.DeepClone() :?> JsonArray
    req

// ── API call ──────────────────────────────────────────────────────────────────

let private postMessages (req: JsonObject) : Task<JsonObject> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        request.Headers.Add("x-api-key", apiKey())
        request.Headers.Add("anthropic-version", "2023-06-01")
        request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31")
        request.Content <- JsonContent.Create(req)
        let! response = httpClient.SendAsync(request)
        let! json = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            failwithf "Anthropic API error %d: %s" (int response.StatusCode) json
        return JsonNode.Parse(json) :?> JsonObject
    }

// ── Core turn ─────────────────────────────────────────────────────────────────

/// Send current message history, return either tool calls or a final text response.
let runTurn (model: string) (temperature: float) (systemPrompt: string option) (tools: ToolDef list) (messages: JsonArray) : Task<TurnResult> =
    task {
        let req = buildRequest model temperature systemPrompt tools messages
        let! resp = postMessages req

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
            return Done(text, inputTokens, outputTokens)
        else
            return ToolCalls(toolUses, inputTokens, outputTokens)
    }

// ── Message history helpers ───────────────────────────────────────────────────

/// Append a simple user text message to the message array in-place.
let appendUserText (messages: JsonArray) (text: string) : JsonArray =
    let msg = JsonObject()
    msg["role"] <- JsonValue.Create("user")
    msg["content"] <- JsonValue.Create(text)
    messages.Add(msg.DeepClone())
    messages

/// Append an assistant turn containing tool_use content blocks.
let appendAssistantToolUse (messages: JsonArray) (calls: ToolCall list) : JsonArray =
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
    messages

/// Append a user turn containing tool_result content blocks.
/// results: list of (toolUseId, resultContent) pairs.
let appendToolResults (messages: JsonArray) (results: (string * string) list) : JsonArray =
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
    messages
