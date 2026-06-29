module TicTacToe.OssDriver.Grader

// Grade one cold-start discovery run against a per-cell ground truth. Ported from grade.py.
// Objective fields (positionTokenSource, bootstrapped, friction, formatErrors) come from the
// WIRE, not self-reports; semantic fields (appIs/goal/...) are keyword-graded with the agent's
// raw text in each rationale for human confirm/override.
//
//   dotnet run --project experiments/oss-driver -- grade \
//     --transcript T.jsonl --gt groundtruth/cell-1010.json [--proxy P.jsonl] [--role X] [--out F]

open System.IO
open System.Text.RegularExpressions
open System.Text.Json
open System.Text.Json.Nodes

let private momentRe = Regex(@"\{""moment"".*?\}", RegexOptions.Singleline)
let private tokens = [ "profile"; "board"; "guess"; "none" ]

let private gstr (o: JsonObject) (k: string) =
    match o.[k] with
    | null -> null
    | v -> (try v.GetValue<string>() with _ -> null)

let private loadReports (path: string) : JsonObject option * JsonObject option =
    let mutable pre = None
    let mutable post = None
    for line in File.ReadLines path do
        let o = try JsonNode.Parse(line) :?> JsonObject |> Some with _ -> None
        match o with
        | Some o when (try o.["role"].GetValue<string>() = "assistant" with _ -> false) ->
            for m in momentRe.Matches(o.["content"].GetValue<string>()) do
                match (try JsonNode.Parse(m.Value) :?> JsonObject |> Some with _ -> None) with
                | Some d when gstr d "appIs" <> "..." && gstr d "myRole" <> "..." ->
                    match gstr d "moment" with
                    | "pre" -> pre <- Some d
                    | "post" -> post <- Some d
                    | _ -> ()
                | _ -> ()
        | _ -> ()
    pre, post

type private Wire = { reads: int; ok: int; rej: int; auth: int; profile: int }

let private wire (path: string option) : Wire =
    match path with
    | None -> { reads = 0; ok = 0; rej = 0; auth = 0; profile = 0 }
    | Some p ->
        let mutable reads, ok, rej, auth, profile = 0, 0, 0, 0, 0
        for raw in File.ReadLines p do
            let line = raw.Trim()
            if line <> "" then
                try
                    use doc = JsonDocument.Parse line
                    let root = doc.RootElement
                    let str (n: string) = match root.TryGetProperty n with | true, v -> v.GetString() | _ -> null
                    let i (n: string) = match root.TryGetProperty n with | true, v -> (try v.GetInt32() with _ -> 0) | _ -> 0
                    match str "event_type" with
                    | null ->                                   // HTTP proxy line
                        let m, status = str "method", i "status"
                        let path = str "path" |> Option.ofObj |> Option.defaultValue ""
                        if path.Contains "/profile" then profile <- profile + 1
                        if status = 302 then auth <- auth + 1
                        elif m = "GET" then reads <- reads + 1
                        elif m = "POST" && (status = 200 || status = 303) then ok <- ok + 1
                        elif m = "POST" && status >= 400 then rej <- rej + 1
                    | "state_read" -> reads <- reads + 1        // ERPC event-log line
                    | "move_accepted" -> ok <- ok + 1
                    | "move_rejected" -> rej <- rej + 1
                    | _ -> ()
                with _ -> ()
        { reads = reads; ok = ok; rej = rej; auth = auth; profile = profile }

let private kw (text: string) (words: string list) =
    let t = (if isNull text then "" else text).ToLowerInvariant()
    words |> List.exists t.Contains

let private vd ok partial = if ok then "correct" elif partial then "partial" else "wrong"

let private vnode verd rat =
    let o = JsonObject()
    o.["verdict"] <- JsonValue.Create(verd: string)
    o.["rationale"] <- JsonValue.Create(rat: string)
    o :> JsonNode

let private normSrc (v: string) =
    let t = (if isNull v then "" else v).ToLowerInvariant()
    match tokens |> List.tryFind t.Contains with
    | Some tok -> tok
    | None -> if t = "" then "none" else t

let run (argv: string[]) : int =
    let valOf name dflt =
        match Array.tryFindIndex ((=) name) argv with
        | Some i when i + 1 < argv.Length -> argv.[i + 1]
        | _ -> dflt
    let tp = valOf "--transcript" null
    let gtp = valOf "--gt" null
    let px = match valOf "--proxy" null with | null -> None | s -> Some s
    let role = valOf "--role" "X"
    let out = valOf "--out" null
    if isNull tp || isNull gtp then
        eprintfn "usage: grade --transcript T.jsonl --gt cell-XXXX.json [--proxy P.jsonl] [--role X] [--out F]"
        2
    else
        let gt = JsonNode.Parse(File.ReadAllText gtp) :?> JsonObject
        let core = gt.["core"] :?> JsonObject
        let acceptable =
            (gt.["expectedPositionTokenSource"] :?> JsonObject).["acceptable"] :?> JsonArray
            |> Seq.map (fun n -> n.GetValue<string>()) |> Set.ofSeq
        let pre, post = loadReports tp
        let w = wire px
        let pre = defaultArg pre (JsonObject())
        let post = defaultArg post (JsonObject())
        let gbool (o: JsonObject) (k: string) = match o.[k] with | null -> None | v -> (try Some(v.GetValue<bool>()) with _ -> None)

        let appIs = gstr pre "appIs"
        let goal = gstr pre "goal"
        let howToJoin = gstr pre "howToJoin"
        let multiReported = match gbool pre "isMultiplayer" with Some b -> b | None -> false
        let multiGt = (try core.["isMultiplayer"].GetValue<bool>() with _ -> true)
        let roleTxt = gstr post "myRole" |> Option.ofObj |> Option.defaultValue ""
        let hedged = kw roleTxt [" or "; "not yet"; "unseat"; "unknown"]
        let affTxt = match post.["myAffordances"] with | null -> "" | v -> v.ToJsonString()
        let src = normSrc (gstr post "positionTokenSource")
        let bootstrapped = w.ok > 0

        let preNode = JsonObject()
        preNode.["appIs"] <- vnode (vd (kw appIs ["tic"; "tac"; "toe"]) (kw appIs ["game"])) (sprintf "reported: %A" appIs)
        preNode.["goal"] <- vnode (vd (kw goal ["row"; "three"; "line"]) (kw goal ["win"; "goal"])) (sprintf "reported: %A" goal)
        preNode.["isMultiplayer"] <- vnode (vd (multiReported = multiGt) false) (sprintf "reported: %b (gt %b)" multiReported multiGt)
        preNode.["howToJoin"] <- vnode (vd (kw howToJoin ["move"; "seat"; "post"]) (not (isNull howToJoin))) (sprintf "reported: %A" howToJoin)

        let postNode = JsonObject()
        postNode.["myRole"] <- vnode (vd (roleTxt.ToLower().Contains(role.ToLower()) && not hedged) (roleTxt.ToLower().Contains(role.ToLower()))) (sprintf "reported: %A (expected %s)" roleTxt role)
        postNode.["canIMove"] <- vnode (vd ((gbool post "canIMove") = Some true) ((gbool post "canIMove").IsSome)) (sprintf "reported: %A (turn-dependent)" (gbool post "canIMove"))
        postNode.["myAffordances"] <- vnode (vd (kw affTxt ["move"; "square"; "play"]) (affTxt <> "")) (sprintf "reported: %s" affTxt)

        // ERPC has no HTTP /profile — "profile" there means the tool schema (the contract),
        // so the /profile-fetch checks don't apply.
        let isErpc = match gt.["arm"] with | null -> false | v -> (try v.GetValue<string>() = "erpc" with _ -> false)
        let flags = JsonArray()
        if not isErpc && src = "profile" && w.profile = 0 then flags.Add(JsonValue.Create "claimed positionTokenSource=profile but never fetched /profile (possible hallucination)")
        if src <> "none" && not (acceptable.Contains src) then flags.Add(JsonValue.Create(sprintf "positionTokenSource=%s not in acceptable %A" src (Set.toList acceptable)))
        let sdExpected =
            (not isErpc) &&
            match gt.["expectedArtifactDiscovery"] with
            | :? JsonArray as a -> a |> Seq.exists (fun n -> n.GetValue<string>().Contains "/profile")
            | _ -> false
        if sdExpected && w.profile = 0 then flags.Add(JsonValue.Create "Sd cell: /profile was never fetched")

        let friction = JsonObject()
        friction.["reads"] <- JsonValue.Create w.reads
        friction.["writesAccepted"] <- JsonValue.Create w.ok
        friction.["writesRejected"] <- JsonValue.Create w.rej
        friction.["authRedirects"] <- JsonValue.Create w.auth
        let reportsPresent = JsonObject()
        reportsPresent.["pre"] <- JsonValue.Create (pre.Count > 0)
        reportsPresent.["post"] <- JsonValue.Create (post.Count > 0)

        let rec' = JsonObject()
        rec'.["cell"] <- JsonValue.Create(gt.["cell"].GetValue<string>())
        rec'.["role"] <- JsonValue.Create role
        rec'.["pre"] <- preNode
        rec'.["post"] <- postNode
        rec'.["positionTokenSource"] <- JsonValue.Create src
        rec'.["positionTokenSourceAcceptable"] <- JsonValue.Create (acceptable.Contains src)
        rec'.["bootstrapped"] <- JsonValue.Create bootstrapped
        rec'.["formatErrors"] <- JsonValue.Create w.rej
        rec'.["friction"] <- friction
        rec'.["profileGets"] <- JsonValue.Create w.profile
        rec'.["flags"] <- flags
        rec'.["reportsPresent"] <- reportsPresent

        let s = rec'.ToJsonString(JsonSerializerOptions(WriteIndented = true))
        if not (isNull out) then File.WriteAllText(out, s + "\n")
        printfn "%s" s
        0
