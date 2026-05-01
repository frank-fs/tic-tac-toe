module TicTacToe.Orchestrator.HttpAgent

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.AnthropicClient
open TicTacToe.Orchestrator.Classifier

let private maxTurns = 50

// ── http_request tool definition ─────────────────────────────────────────────

let private httpRequestTool : ToolDef =
    { Name = "http_request"
      Description = "Make an HTTP request to the tic-tac-toe server. Follow links in responses to discover available actions. Read response bodies carefully — they contain affordances (links, forms, JSON fields) that tell you what to do next."
      InputSchema =
        JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "method": { "type": "string", "enum": ["GET","POST","DELETE"] },
            "url": { "type": "string", "description": "Full URL, e.g. http://localhost:5228/arenas" },
            "headers": { "type": "object", "description": "Optional headers as string key-value pairs" },
            "body": { "type": "string", "description": "Request body (URL-encoded form data or JSON)" }
          },
          "required": ["method","url"]
        }""") }

// ── HTTP execution ────────────────────────────────────────────────────────────

let private executeHttp (httpClient: HttpClient) (call: ToolCall) : Task<int * Map<string,string> * string> =
    task {
        let input = call.Input
        let method = input["method"].GetValue<string>()
        let url = input["url"].GetValue<string>()

        use req = new HttpRequestMessage(Method = HttpMethod(method), RequestUri = Uri(url))

        if input.ContainsKey("headers") then
            let hdrs = input["headers"] :?> JsonObject
            for prop in hdrs do
                req.Headers.TryAddWithoutValidation(prop.Key, prop.Value.GetValue<string>()) |> ignore

        if input.ContainsKey("body") then
            let bodyStr = input["body"].GetValue<string>()
            req.Content <- new StringContent(bodyStr, Encoding.UTF8)
            req.Content.Headers.ContentType <- MediaTypeHeaderValue("application/x-www-form-urlencoded")

        let! resp = httpClient.SendAsync(req)
        let statusCode = int resp.StatusCode
        let responseHeaders =
            resp.Headers
            |> Seq.map (fun kvp -> kvp.Key, String.concat "," kvp.Value)
            |> Map.ofSeq
        let! responseBody = resp.Content.ReadAsStringAsync()
        return (statusCode, responseHeaders, responseBody)
    }

// ── Game loop ─────────────────────────────────────────────────────────────────

/// Run one game using E0 or E1 setup.
/// systemPrompt: None for E0 (bare URL), Some prompt for E1.
/// Returns (transcript entries, total tokens consumed).
let runGame
    (httpClient: HttpClient)
    (model: string)
    (temperature: float)
    (systemPrompt: string option)
    (baseUrl: string)
    : Task<TranscriptEntry list * int> =
    task {
        let messages = JsonArray()
        let initialMsg =
            match systemPrompt with
            | Some _ -> sprintf "Here is a URL: %s" baseUrl
            | None -> baseUrl
        appendUserText messages initialMsg |> ignore

        let mutable transcript: HttpEntry list = []
        let mutable priorBodies: string list = []
        let mutable priorRequests: (string * string) list = []
        let mutable openApiPaths: string list = []
        let mutable totalTokens = 0
        let mutable turn = 0
        let mutable keepGoing = true

        while keepGoing && turn < maxTurns do
            let! result = runTurn model temperature systemPrompt [httpRequestTool] messages
            match result with
            | Done(_, inp, out) ->
                totalTokens <- totalTokens + inp + out
                keepGoing <- false

            | ToolCalls(calls, inp, out) ->
                totalTokens <- totalTokens + inp + out
                appendAssistantToolUse messages calls |> ignore

                let toolResults = Collections.Generic.List<string * string>()

                for call in calls do
                    turn <- turn + 1
                    let! (statusCode, responseHeaders, responseBody) = executeHttp httpClient call

                    let url = call.Input["url"].GetValue<string>()
                    let method = call.Input["method"].GetValue<string>()

                    // Track OpenAPI doc paths for strategy classification
                    if url.Contains("openapi") && statusCode < 400 then
                        try
                            let doc = JsonNode.Parse(responseBody)
                            let paths = doc["paths"] :?> JsonObject
                            for p in paths do
                                openApiPaths <- p.Key :: openApiPaths
                        with _ -> ()

                    let outcome = classifyOutcome method url statusCode priorRequests
                    let urlInSpec = openApiPaths |> List.exists (fun p -> url.Contains(p))
                    let strategy = classifyStrategy method url priorBodies urlInSpec

                    let requestHeaders =
                        if call.Input.ContainsKey("headers") then
                            (call.Input["headers"] :?> JsonObject)
                            |> Seq.map (fun p -> p.Key, p.Value.GetValue<string>())
                            |> Map.ofSeq
                        else Map.empty

                    let entry = {
                        Turn = turn
                        Method = method
                        Url = url
                        RequestHeaders = requestHeaders
                        RequestBody =
                            if call.Input.ContainsKey("body") then Some(call.Input["body"].GetValue<string>())
                            else None
                        StatusCode = statusCode
                        ResponseHeaders = responseHeaders
                        ResponseBody = responseBody
                        Outcome = outcome
                        Strategy = strategy
                    }

                    transcript <- transcript @ [entry]
                    priorBodies <- responseBody :: priorBodies
                    priorRequests <- (method, url) :: priorRequests
                    toolResults.Add(call.Id, sprintf "HTTP %d\n%s" statusCode responseBody)

                appendToolResults messages (toolResults |> Seq.toList) |> ignore

        return (transcript |> List.map Http, totalTokens)
    }
