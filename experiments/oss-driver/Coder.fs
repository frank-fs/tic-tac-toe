module TicTacToe.OssDriver.Coder

// Behavior-coding: per-action browser-user vs API-client labels for a seat transcript.
// Mechanical string-match against the immediately-preceding rendered page; an optional LLM
// judge (via LlmClient, --llm-judge) re-labels only ambiguous WAIT actions. See behavior-coding.md.
//   dotnet run --project experiments/oss-driver -- code --transcript <seat.transcript.jsonl> --role X

open System
open System.IO
open System.Text.RegularExpressions
open System.Text.Json
open System.Text.Json.Nodes
open TicTacToe.OssDriver.Types

let private actionRe = Regex(@"\b(GET|POST)\s+(/\S+)(?:\s+(.*\S))?", RegexOptions.IgnoreCase)

/// (role, content) turns from a transcript, skipping malformed lines.
let private readTurns (path: string) : (string * string) list =
    File.ReadAllLines path
    |> Array.choose (fun line ->
        match (try JsonNode.Parse(line) :?> JsonObject |> Some with _ -> None) with
        | Some o ->
            let g (k: string) = o.[k] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
            Some(g "role", g "content")
        | None -> None)
    |> Array.toList

/// Rendered HTML from a user turn ("HTTP <status>\n<headers>\n\n<body>").
let private pageBody (userContent: string) : string =
    let i = userContent.IndexOf("\n\n")
    if i >= 0 then userContent.Substring(i + 2) else userContent

let private matchesOf (pat: string) (html: string) =
    Regex.Matches(html, pat) |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq

/// Parse the action line (reasoning precedes it) -> (verb, path, body).
let private parseAction (content: string) : (string * string * string) option =
    let m = actionRe.Match content
    if m.Success then
        Some(m.Groups.[1].Value.ToUpperInvariant(), m.Groups.[2].Value,
             (if m.Groups.[3].Success then m.Groups.[3].Value else ""))
    else None

let private bodyParams (body: string) : (string * string) list =
    body.Split('&')
    |> Array.choose (fun kv -> match kv.Split('=') with | [| k; v |] -> Some(k.Trim(), v.Trim()) | _ -> None)
    |> Array.toList

/// GET: re-read (READ) vs waiting off-turn (WAIT). Turn read from the page's status text.
let private codeGet (prevPage: string) (role: string) : string =
    let turn = Regex.Match(prevPage, @"class=""status"">([^<]*)").Groups.[1].Value
    if turn = "" then "READ" elif turn.StartsWith(role) then "READ" else "WAIT"

/// POST: FOLLOW iff path is a form action on the prior page AND player/position values were offered.
let private codePost (path: string) (body: string) (prevPage: string) : string =
    if path.EndsWith "/delete" || path.EndsWith "/restart" then "DESTRUCT"
    elif not (Set.contains path (matchesOf @"action=""([^""]+)""" prevPage)) then "INVENT_PATH"
    else
        let vals = matchesOf @"value=""([^""]+)""" prevPage
        let ps = bodyParams body
        let names = ps |> List.map fst |> Set.ofList
        let hasMoveFields = Set.contains "player" names && Set.contains "position" names
        let offered = ps |> List.forall (fun (k, v) -> (k = "player" || k = "position") && Set.contains v vals)
        if hasMoveFields && offered then "FOLLOW" else "INVENT_PARAM"

/// Walk the transcript, tracking the most-recent rendered page, emitting one code per action.
let private codeActions (turns: (string * string) list) (role: string) : (string * string * string) list =
    let mutable prevPage = ""
    let acc = ResizeArray<string * string * string>()   // (code, verb, path)
    for (r, content) in turns do        // bounded by transcript length
        if r = "user" then prevPage <- pageBody content
        elif r = "assistant" then
            match parseAction content with
            | Some("GET", path, _)      -> acc.Add(codeGet prevPage role, "GET", path)
            | Some("POST", path, body)  -> acc.Add(codePost path body prevPage, "POST", path)
            | _ -> ()
    List.ofSeq acc

let private positionTokenSource (turns: (string * string) list) : string =
    // Only the model's own MOMENT report (assistant turns); skip the system prompt's
    // literal "profile|board|guess|none" template (contains '|').
    turns
    |> List.filter (fun (r, _) -> r = "assistant")
    |> List.tryPick (fun (_, c) ->
        Regex.Matches(c, @"""positionTokenSource""\s*:\s*""([^""]+)""")
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> Seq.tryFind (fun v -> not (v.Contains "|")))
    |> Option.defaultValue "none"

/// LLM judge (opt-in) re-labels WAIT actions as patient-wait (WAIT) or stuck-loop (STUCK).
let private judgeWaits (model: string) (nWait: int) : string =
    if nWait = 0 then "n/a"
    else
        let sys = "You classify an agent's repeated page re-reads while waiting for an opponent's move. Reply ONE word: PATIENT if the re-reads are reasonable waiting, STUCK if it is looping without progress."
        let usr = sprintf "The seat issued %d GET re-reads while it was not its turn. Was this patient waiting or stuck looping? One word." nWait
        try (LlmClient.chat (Backend.autoDetect ()) model [ "system", sys; "user", usr ]).Trim().ToUpperInvariant()
        with _ -> "PATIENT"

let private codeSeat (path: string) (role: string) (model: string) (llm: bool) : JsonObject =
    let turns = readTurns path
    let coded = codeActions turns role
    let count c = coded |> List.filter (fun (k, _, _) -> k = c) |> List.length
    let nFollow, nInvent = count "FOLLOW", count "INVENT_PATH" + count "INVENT_PARAM"
    let denom = nFollow + nInvent
    let ratio = if denom = 0 then -1.0 else float nFollow / float denom
    let label =
        if ratio < 0.0 then "no-moves"
        elif ratio >= 0.8 then "browser-user"
        elif ratio <= 0.2 then "api-client"
        else "mixed"
    let o = JsonObject()
    o.["role"] <- JsonValue.Create role
    o.["transcript"] <- JsonValue.Create(Path.GetFileName path)
    let counts = JsonObject()
    for c in [ "FOLLOW"; "READ"; "WAIT"; "INVENT_PATH"; "INVENT_PARAM"; "DESTRUCT" ] do
        counts.[c] <- JsonValue.Create(count c)
    o.["counts"] <- counts
    o.["conduct_ratio"] <- JsonValue.Create(if ratio < 0.0 then nan else Math.Round(ratio, 3))
    o.["label"] <- JsonValue.Create label
    o.["positionTokenSource"] <- JsonValue.Create(positionTokenSource turns)
    if llm then o.["wait_judgment"] <- JsonValue.Create(judgeWaits model (count "WAIT"))
    o

let run (argv: string[]) : int =
    let valOf name dflt =
        match Array.tryFindIndex ((=) name) argv with
        | Some i when i + 1 < argv.Length -> argv.[i + 1]
        | _ -> dflt
    let tp = valOf "--transcript" null
    let role = valOf "--role" "X"
    let model = valOf "--model" (LlmClient.defaultModel (Backend.autoDetect ()))
    let llm = Array.contains "--llm-judge" argv
    if isNull tp then eprintfn "code: --transcript <path> required"; 2
    elif not (File.Exists tp) then eprintfn "code: transcript not found: %s" tp; 2
    else
        printfn "%s" ((codeSeat tp role model llm).ToJsonString())
        0
