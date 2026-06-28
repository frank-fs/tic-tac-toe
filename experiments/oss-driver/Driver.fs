module TicTacToe.OssDriver.Driver

// ReAct player loop. The model navigates the interface itself: it reads raw HTTP
// responses and decides the next request; this driver only plumbs HTTP + paces.
// Identity = one persistent cookie session per process (= one seat), which strips the
// bash-statelessness cookie-jar artifact so what's tested is the INTERFACE.

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open TicTacToe.OssDriver.Types

type Config =
    { Backend: Backend
      Model: string
      Role: string          // "X" | "O"
      Persona: Persona
      Base: string          // arena PROXY base, e.g. http://localhost:6228
      Route: string         // "games" | "arenas"
      Game: string          // id, or "" to discover
      ColdStart: bool       // true = no role/app/format hints; model discovers the app from the surface
      MaxActions: int
      MaxMoves: int
      Window: int
      PollSeconds: float }

// One HttpClient with a cookie jar (identity) and NO auto-redirect (see real 302/303).
let private newClient () =
    let handler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = true, CookieContainer = CookieContainer())
    new HttpClient(handler, Timeout = TimeSpan.FromSeconds 30.0)

let private actionRe =
    Regex(@"\b(GET|POST)\s+(/\S+)(?:\s+(.*\S))?", RegexOptions.IgnoreCase)

let parseAction (text: string) : (string * string * string option) option =
    let m = actionRe.Match(text.Replace("`", " "))
    if m.Success then
        let body =
            if m.Groups.[3].Success && m.Groups.[3].Value.Trim() <> "" then Some(m.Groups.[3].Value.Trim())
            else None
        Some(m.Groups.[1].Value.ToUpperInvariant(), m.Groups.[2].Value, body)
    else
        None

let private terminalTokens =
    [ "\"status\":\"xwins\""; "\"status\":\"owins\""; "\"status\":\"draw\""
      "data-game-status=\"won\""; "data-game-status=\"draw\""; "it's a draw"; " wins"; "x won"; "o won" ]

// A 404 is terminal ONLY on the game resource itself (the game vanished). During cold-start
// the agent probes unknown paths (/status, /profile, ...); a 404 there is exploration
// feedback, NOT game-over — treating it as terminal aborts discovery prematurely.
let private terminal (gamePath: string) (path: string) (status: int) (body: string) : string option =
    if status = 404 then
        if path.TrimEnd('/') = gamePath.TrimEnd('/') then Some "game_not_found" else None
    else
        let low = body.ToLowerInvariant()
        if terminalTokens |> List.exists low.Contains then Some "over" else None

// A real HTTP client sees response headers; the agent must too, or Sd (which
// publishes discovery via Link/Allow headers + /profile) is always a miss.
// Surface ALL response headers, curl -i style; uniform (not a Link/Allow subset)
// so the observation isn't hand-biased toward Sd.
let private formatHeaders (resp: HttpResponseMessage) : string =
    let lines (h: System.Net.Http.Headers.HttpHeaders) =
        h |> Seq.map (fun kv -> sprintf "%s: %s" kv.Key (String.Join(", ", kv.Value)))
    Seq.append (lines resp.Headers) (lines resp.Content.Headers) |> String.concat "\n"

let private gameReq (client: HttpClient) (baseUrl: string) (m: string) (path: string) (body: string option) : int * string * string =
    let url = baseUrl.TrimEnd('/') + path
    use req = new HttpRequestMessage(HttpMethod(m), url)
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/x-www-form-urlencoded"))
    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/html") |> ignore
    try
        use resp = client.Send req
        int resp.StatusCode, formatHeaders resp, resp.Content.ReadAsStringAsync().Result
    with e ->
        0, "", sprintf "<request error: %s>" e.Message

/// Seed the session cookie (identity) and resolve the game path.
let private seed (client: HttpClient) (cfg: Config) : string =
    gameReq client cfg.Base "GET" "/login" None |> ignore
    let gid =
        if cfg.Game <> "" then cfg.Game
        else
            let _, _, home = gameReq client cfg.Base "GET" "/" None
            let m = Regex.Match(home, sprintf @"%s/([0-9a-f-]{36})" cfg.Route)
            if m.Success then m.Groups.[1].Value else ""
    if gid = "" then failwith "could not discover game id"
    sprintf "/%s/%s" cfg.Route gid

// Prompt files are the single source of truth — loaded verbatim, never inlined, so a session
// cannot silently weaken them. Each has a committed .sha256 lock; verifyPromptLock fails the
// run loudly if the file's bytes drift from the lock. Update a lock only as a reviewed commit.
let private coldStartPromptPath =
    Environment.GetEnvironmentVariable "COLDSTART_PROMPT_PATH"
    |> Option.ofObj |> Option.defaultValue "experiments/oss-driver/coldstart-prompt.md"

let private directedPromptPath =
    Environment.GetEnvironmentVariable "DIRECTED_PROMPT_PATH"
    |> Option.ofObj |> Option.defaultValue "experiments/oss-driver/directed-prompt.md"

let private directedFactsPath =
    Environment.GetEnvironmentVariable "DIRECTED_FACTS_PATH"
    |> Option.ofObj |> Option.defaultValue "experiments/oss-driver/directed-facts.json"

let private verifyPromptLock (promptPath: string) =
    let lockPath = promptPath + ".sha256"
    if IO.File.Exists lockPath then
        let expected = (IO.File.ReadAllText lockPath).Trim().Split([| ' '; '\t' |]).[0]
        use sha = System.Security.Cryptography.SHA256.Create()
        let actual =
            IO.File.ReadAllBytes promptPath |> sha.ComputeHash |> Array.map (sprintf "%02x") |> String.concat ""
        if actual <> expected then
            failwithf "prompt hash mismatch (frozen prompt changed):\n  file     %s\n  expected %s\n  actual   %s\nIf intentional, regenerate %s in a reviewed commit; otherwise restore the prompt." promptPath expected actual lockPath

let private loadPrompt (path: string) : string =
    if not (IO.File.Exists path) then
        failwithf "prompt not found: %s (cwd %s)" path (IO.Directory.GetCurrentDirectory())
    verifyPromptLock path
    IO.File.ReadAllText path

// Cold-start (discovery): verbatim frozen instrument (MOMENT 1/2, positionTokenSource);
// {URL} is the only substitution. The model is told nothing about the app.
let private coldStartPrompt (_cfg: Config) (gamePath: string) : string =
    (loadPrompt coldStartPromptPath).Replace("{URL}", gamePath)

// Directed (play): the model is GIVEN the full correct facts (directed-facts.json) so play
// skill is measured independent of discovery. The template carries no app specifics — they
// are substituted from the facts, so tic-tac-toe appears only because the facts put it there.
let private systemPrompt (cfg: Config) (gamePath: string) : string =
    let facts = JsonNode.Parse(IO.File.ReadAllText directedFactsPath) :?> JsonObject
    let f (key: string) = facts[key].GetValue<string>()
    let first = if cfg.Role = "O" then "X moves first — you may have to wait for your first turn." else ""
    (loadPrompt directedPromptPath)
        .Replace("{URL}", gamePath)
        .Replace("{ROLE}", cfg.Role)
        .Replace("{FIRST}", first)
        .Replace("{APPIS}", f "appIs")
        .Replace("{GOAL}", f "goal")
        .Replace("{HOWTOACT}", f "howToAct")

let private window (messages: ResizeArray<string * string>) (n: int) : (string * string) list =
    let sys = messages.[0]
    let rest = messages |> Seq.skip 1 |> Seq.toList
    let tail = if List.length rest > n then rest |> List.skip (List.length rest - n) else rest
    sys :: tail

/// Play one seat to completion (or cap). Returns a JSON result string.
let run (cfg: Config) : string =
    use client = newClient ()
    let gamePath = seed client cfg
    let messages = ResizeArray<string * string>()
    messages.Add("system", (if cfg.ColdStart then coldStartPrompt else systemPrompt) cfg gamePath)
    messages.Add("user", if cfg.ColdStart then "Begin." else "Begin: read the current state, then act.")
    let mutable moves = 0
    let mutable outcome = "incomplete"
    let mutable step = 0
    let mutable stop = false
    while not stop && step < cfg.MaxActions do
        let reply =
            try LlmClient.chat cfg.Backend cfg.Model (window messages cfg.Window)
            with e -> sprintf "<chat error: %s>" e.Message
        messages.Add("assistant", reply)
        match parseAction reply with
        | None ->
            if not (isNull (Environment.GetEnvironmentVariable "DRIVER_DEBUG")) then
                eprintfn "[%s] UNPARSEABLE: %s" cfg.Role (reply.Replace("\n", " ").[.. min 200 (reply.Length - 1)])
            messages.Add("user", "I couldn't parse an action. Reply with exactly one line: GET <path> or POST <path> <body>.")
        | Some(m, path, body) ->
            let status, headers, text = gameReq client cfg.Base m path body
            // Count only ACCEPTED moves toward the move cap; rejected POSTs (4xx, e.g. format
            // fumbling) are bounded by MaxActions, not the move cap — else a fumbling agent
            // burns its move budget on rejects and stops before completing the task.
            if m = "POST" && status < 400 then moves <- moves + 1
            if not (isNull (Environment.GetEnvironmentVariable "DRIVER_DEBUG")) then
                eprintfn "[%s] %s %s %s -> %d" cfg.Role m path (defaultArg body "") status
            let obs = if text.Length <= 4000 then text else text.[..3999] + " …[truncated]"
            messages.Add("user", sprintf "HTTP %d\n%s\n\n%s" status headers obs)
            match terminal gamePath path status text with
            | Some o -> outcome <- o; stop <- true
            | None ->
                if moves >= cfg.MaxMoves then
                    outcome <- "move_cap"
                    stop <- true
                elif m = "GET" then
                    Threading.Thread.Sleep(int (cfg.PollSeconds * 1000.0))
        step <- step + 1

    // Persist the full transcript (incl. MOMENT 1/2 discovery reports, which are assistant
    // replies, not actions) when TRANSCRIPT_PATH is set — that IS the discovery data to grade.
    match Environment.GetEnvironmentVariable "TRANSCRIPT_PATH" with
    | null | "" -> ()
    | path ->
        use w = new IO.StreamWriter(path)
        for (role, content) in messages do
            let o = JsonObject()
            o["role"] <- JsonValue.Create role
            o["content"] <- JsonValue.Create content
            w.WriteLine(o.ToJsonString())

    let res = JsonObject()
    res["role"] <- JsonValue.Create cfg.Role
    res["persona"] <- JsonValue.Create cfg.Persona.Name
    res["model"] <- JsonValue.Create cfg.Model
    res["game"] <- JsonValue.Create gamePath
    res["actions"] <- JsonValue.Create step
    res["moves_submitted"] <- JsonValue.Create moves
    res["outcome"] <- JsonValue.Create outcome
    res.ToJsonString()
