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

let private terminal (status: int) (body: string) : string option =
    if status = 404 then Some "game_not_found"
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

let private systemPrompt (cfg: Config) (gamePath: string) : string =
    let first = if cfg.Role = "O" then "X moves first — you may have to wait for your first turn." else ""
    sprintf
        "You are the %s player in a live two-player tic-tac-toe game. Play to win, drive the game to completion. %s\n\n\
         You interact by issuing ONE HTTP request per reply, against the game at %s. Reply with EXACTLY one line, nothing else:\n\
         \  GET %s\n\
         or\n\
         \  POST %s <url-encoded-body>\n\n\
         I run the request and reply with the response status + body. Learn how to read the board and how to format a move from what the server returns.\n\n\
         Rules:\n\
         - Only submit a move on YOUR turn (%s); between turns, GET to re-read the state.\n\
         - Only play a square that is currently EMPTY.\n\
         - It's not a race — re-read, don't spam. A 404 after the game started means it ended; stop.\n\n\
         %s\n\nBegin by reading the current state."
        cfg.Role first gamePath gamePath gamePath cfg.Role cfg.Persona.Guidance

// Cold-start prompt: the model is told ONLY the entry path, the one-line HTTP I/O contract,
// and the protocol floor. NOT what the app is, NOT its role-as-player, NOT the move format —
// those are exactly what it must discover from the served surface (the variable Sd supplies).
// Identity is driver-owned transport (one cookie session, seeded), so the model never logs in.
let private coldStartPrompt (cfg: Config) (gamePath: string) : string =
    sprintf
        "You have been given ONE thing: a live web app, whose entry point is %s (other paths share the same host). \
         You know nothing else about it — not what it is, not how it works. Figure out what it is, then interact with \
         it toward whatever its goal turns out to be.\n\n\
         You act by issuing ONE HTTP request per reply — EXACTLY one line, nothing else:\n\
         \  GET <path>\n\
         or\n\
         \  POST <path> <url-encoded-body>\n\
         I run it and reply with the HTTP status and body. Your session identity (cookies) is already handled — never \
         log in, just act.\n\n\
         DISCOVER everything else from what the server returns: what the app is, how to read its state, and how to \
         format any action you take. None of that is given to you here.\n\n\
         Floor (true of any turn-based, multi-party app): act only when it is your turn; GET to re-read between turns; \
         it's not a race, don't spam; a 404 after it began means it ended — stop.\n\n\
         Once you understand the goal, pursue it well and drive the interaction to completion.\n\n\
         Begin by reading %s."
        gamePath gamePath

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
    messages.Add("user", "Begin: read the current state, then act.")
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
            if m = "POST" then moves <- moves + 1
            let status, headers, text = gameReq client cfg.Base m path body
            if not (isNull (Environment.GetEnvironmentVariable "DRIVER_DEBUG")) then
                eprintfn "[%s] %s %s %s -> %d" cfg.Role m path (defaultArg body "") status
            let obs = if text.Length <= 4000 then text else text.[..3999] + " …[truncated]"
            messages.Add("user", sprintf "HTTP %d\n%s\n\n%s" status headers obs)
            match terminal status text with
            | Some o -> outcome <- o; stop <- true
            | None ->
                if moves >= cfg.MaxMoves then
                    outcome <- "move_cap"
                    stop <- true
                elif m = "GET" then
                    Threading.Thread.Sleep(int (cfg.PollSeconds * 1000.0))
        step <- step + 1

    let res = JsonObject()
    res["role"] <- JsonValue.Create cfg.Role
    res["persona"] <- JsonValue.Create cfg.Persona.Name
    res["model"] <- JsonValue.Create cfg.Model
    res["game"] <- JsonValue.Create gamePath
    res["actions"] <- JsonValue.Create step
    res["moves_submitted"] <- JsonValue.Create moves
    res["outcome"] <- JsonValue.Create outcome
    res.ToJsonString()
