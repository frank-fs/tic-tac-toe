module TicTacToe.DiscoveryHarness.Driver

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading
open TicTacToe.OssDriver.Types
open TicTacToe.OssDriver
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

type SeatConfig =
    { Backend: Backend; Model: string; Seat: string; Persona: Persona
      Base: string; MaxActions: int; MaxMoves: int; Window: int; PollSeconds: float
      StartGate: ManualResetEventSlim option; SeatedSignal: (unit -> unit) option }

let private newClient () =
    let h = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = true, CookieContainer = CookieContainer())
    new HttpClient(h, Timeout = TimeSpan.FromSeconds 30.0)

// Path is /\S* (not /\S+) so a bare "GET /" — fetching the base-URL root, the most
// basic cold-start action — parses to path "/". With /\S+ the root was unfetchable.
let private actionRe = Regex(@"\b(GET|POST)\s+(/\S*)(?:\s+(.*\S))?", RegexOptions.IgnoreCase)

let parseAction (text: string) : (string * string * string option) option =
    let m = actionRe.Match(text.Replace("`", " "))
    if not m.Success then None
    else
        let body = if m.Groups.[3].Success && m.Groups.[3].Value.Trim() <> "" then Some(m.Groups.[3].Value.Trim()) else None
        Some(m.Groups.[1].Value.ToUpperInvariant(), m.Groups.[2].Value, body)

let private terminalTokens =
    [ "data-game-status=\"won\""; "data-game-status=\"draw\""; "wins!"; "it's a draw" ]

// A 404 in cold start means "that path does not exist" — the agent must be free to
// try another path, NOT stop. Simple never 404s a live game, so ONLY win/draw prose
// is terminal; MaxActions + the move cap bound the loop otherwise.
let private terminalOutcome (_status: int) (body: string) : string option =
    let low = body.ToLowerInvariant()
    if terminalTokens |> List.exists low.Contains then Some "over" else None

let private send (client: HttpClient) (baseUrl: string) (m: string) (path: string) (body: string option) : int * string =
    let url = baseUrl.TrimEnd('/') + path
    use req = new HttpRequestMessage(HttpMethod(m), url)
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/x-www-form-urlencoded"))
    req.Headers.TryAddWithoutValidation("Accept", "text/html") |> ignore   // HTML-only — no JSON shortcut
    try use resp = client.Send req in int resp.StatusCode, resp.Content.ReadAsStringAsync().Result
    with e -> 0, sprintf "<request error: %s>" e.Message

let private window (messages: ResizeArray<string * string>) (n: int) : (string * string) list =
    let sys = messages.[0]
    let rest = messages |> Seq.skip 1 |> Seq.toList
    let tail = if List.length rest > n then rest |> List.skip (List.length rest - n) else rest
    sys :: tail

let private captureReports (t: Transcript) (reply: string) =
    if t.Discovery.IsNone then Transcript.tryParseDiscovery reply |> Option.iter (fun d -> t.Discovery <- Some d)
    if t.Role.IsNone then Transcript.tryParseRole reply |> Option.iter (fun r -> t.Role <- Some r)

let runSeat (cfg: SeatConfig) : Transcript =
    use client = newClient ()
    let t = Transcript.empty cfg.Seat cfg.Persona.Name cfg.Model
    let messages = ResizeArray<string * string>()
    messages.Add("system", ColdStart.systemPrompt cfg.Base cfg.Persona)
    // Identity is driver-owned (cookie jar): establish it once via the conventional
    // login route — not an agent-visible action — then hand the agent the SERVED
    // content of the base URL it was told to review. Faithful to "review the app at
    // this URL", and removes the artificial "agent must guess to even start" hurdle.
    send client cfg.Base "GET" "/login" None |> ignore
    let seedStatus, seedBody = send client cfg.Base "GET" "/" None
    t.Requests.Add { Method = "GET"; Path = "/"; Body = None; Status = seedStatus
                     BodySnippet = (if seedBody.Length <= 300 then seedBody else seedBody.[..299]) }
    HtmlBoard.parse seedBody |> Option.iter (fun cells -> t.Boards.Add { AfterRequestIndex = 0; Cells = cells })
    let seedObs = if seedBody.Length <= 4000 then seedBody else seedBody.[..3999] + " …[truncated]"
    messages.Add("user", sprintf "You fetched the base URL. HTTP %d\n%s\n\nReview this, then reply with your DISCOVERY report or your next request." seedStatus seedObs)
    t.Actions <- t.Actions + 1
    let mutable firstSeated = false
    let mutable stop = false
    while not stop && t.Actions < cfg.MaxActions do
        let reply = try LlmClient.chat cfg.Backend cfg.Model (window messages cfg.Window)
                    with e -> sprintf "<chat error: %s>" e.Message
        messages.Add("assistant", reply)
        captureReports t reply
        match parseAction reply with
        | None -> messages.Add("user", "Reply with one line: a DISCOVERY/ROLE JSON report, or GET <path>, or POST <path> <body>.")
        | Some(m, path, body) ->
            if m = "POST" && not firstSeated then cfg.StartGate |> Option.iter (fun g -> g.Wait())
            let status, text = send client cfg.Base m path body
            let reqIndex = t.Requests.Count
            t.Requests.Add { Method = m; Path = path; Body = body; Status = status
                             BodySnippet = (if text.Length <= 300 then text else text.[..299]) }
            HtmlBoard.parse text |> Option.iter (fun cells -> t.Boards.Add { AfterRequestIndex = reqIndex; Cells = cells })
            if m = "POST" then
                t.MovesSubmitted <- t.MovesSubmitted + 1
                if status < 400 && not firstSeated then firstSeated <- true; cfg.SeatedSignal |> Option.iter (fun f -> f())
            let obs = if text.Length <= 4000 then text else text.[..3999] + " …[truncated]"
            messages.Add("user", sprintf "HTTP %d\n%s" status obs)
            match terminalOutcome status text with
            | Some o -> t.Outcome <- o; stop <- true
            | None ->
                if t.MovesSubmitted >= cfg.MaxMoves then t.Outcome <- "move_cap"; stop <- true
                elif m = "GET" then Thread.Sleep(int (cfg.PollSeconds * 1000.0))
        t.Actions <- t.Actions + 1
    t
