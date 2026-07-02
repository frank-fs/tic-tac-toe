module TicTacToe.OssDriver.Quality

// Play-quality metric. Tic-tac-toe is SOLVED: two attentive, strategy-aware players draw. But a
// win is legitimate and the WINNER is not a failure — taking a win the opponent hands you is
// optimal play. The failure is specific and attributable: the LOSER's missed block (or missed
// win), a single player's move, not the game's outcome. So the quality axis is per-player
// blunder-free play, NOT the outcome label (counting wins as the agent's goal is what's
// dishonest — the goal is correct play, whose modal result between equals is the draw).
//
// This replays a game's accepted moves through an exact 3x3 minimax solver (deterministic, no
// LLM judge) and reports:
//   - outcome: purely descriptive (draw / x_wins / o_wins / incomplete)
//   - per-role missed-win / missed-block / suboptimal + `clean` (did THIS player blunder?)
// A clean winner (0 blunders, opponent missed a block) is a strong game, not a failure.
//
// Source is the server request log's ordered `move_accepted {role,move}` + `game_over {outcome}`
// lines (authoritative server truth, cell-independent — no HTML board parsing, no self-report).
//
//   dotnet run --project experiments/oss-driver -- quality --log arena-surface.jsonl [--game <id>] [--out F]

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

// Board: 9 cells, row-major index 0..8. 0 = empty, 1 = X, 2 = O.
let private posIndex =
    [ "TopLeft", 0; "TopCenter", 1; "TopRight", 2
      "MiddleLeft", 3; "MiddleCenter", 4; "MiddleRight", 5
      "BottomLeft", 6; "BottomCenter", 7; "BottomRight", 8 ]
    |> Map.ofList

let private lines =
    [ [ 0; 1; 2 ]; [ 3; 4; 5 ]; [ 6; 7; 8 ]
      [ 0; 3; 6 ]; [ 1; 4; 7 ]; [ 2; 5; 8 ]
      [ 0; 4; 8 ]; [ 2; 4; 6 ] ]

let private winner (b: int[]) : int =
    lines
    |> List.tryPick (fun l ->
        match l |> List.map (fun i -> b.[i]) with
        | [ a; c; d ] when a <> 0 && a = c && c = d -> Some a
        | _ -> None)
    |> Option.defaultValue 0

let private legal (b: int[]) = [ for i in 0..8 do if b.[i] = 0 then yield i ]

/// Empty cells where placing p immediately completes a line (an available win for p).
let private winningCells (b: int[]) (p: int) =
    legal b
    |> List.filter (fun i -> let c = Array.copy b in c.[i] <- p; winner c = p)
    |> Set.ofList

/// Minimax value of board `b` with `p` to move, from p's perspective: +1 win, 0 draw, -1 loss.
/// Bounded: recursion depth <= empty-cell count <= 9.
let rec private minimax (b: int[]) (p: int) : int =
    match winner b with
    | w when w <> 0 -> if w = p then 1 else -1
    | _ ->
        match legal b with
        | [] -> 0
        | moves ->
            moves
            |> List.map (fun i -> let c = Array.copy b in c.[i] <- p; -(minimax c (3 - p)))
            |> List.max

let private moveValue (b: int[]) (p: int) (i: int) =
    let c = Array.copy b in c.[i] <- p
    if winner c = p then 1 else -(minimax c (3 - p))

/// Legal moves whose minimax value equals the best achievable (the optimal set).
let private optimalCells (b: int[]) (p: int) =
    let vs = legal b |> List.map (fun i -> i, moveValue b p i)
    let best = vs |> List.map snd |> List.max
    vs |> List.filter (fun (_, v) -> v = best) |> List.map fst |> Set.ofList

type private Tally = { mutable moves: int; mutable missedWin: int; mutable missedBlock: int; mutable suboptimal: int }
let private tally () = { moves = 0; missedWin = 0; missedBlock = 0; suboptimal = 0 }

/// Ordered (role, cell) accepted moves + outcome for one game_id, read from the request log.
let private readGame (path: string) (wantGame: string option) : (string * int) list * string =
    let mutable moves = []          // reversed; reversed back at end
    let mutable outcome = "incomplete"
    let mutable lastOverGame = None
    let parsed =
        File.ReadLines path
        |> Seq.choose (fun line -> try JsonNode.Parse(line) :?> JsonObject |> Some with _ -> None)
        |> Seq.toList             // bounded by log length
    // Default target: the game that reached game_over (else the first move's game).
    for o in parsed do
        match (try o.["event_type"].GetValue<string>() with _ -> "") with
        | "game_over" -> lastOverGame <- Some(o.["game_id"].GetValue<string>())
        | _ -> ()
    let target =
        match wantGame, lastOverGame with
        | Some g, _ -> Some g
        | None, some -> some
    let gameOf (o: JsonObject) = try o.["game_id"].GetValue<string>() |> Some with _ -> None
    let matches o = match target, gameOf o with | Some t, Some g -> t = g | None, _ -> true | _ -> false
    for o in parsed do
        if matches o then
            match (try o.["event_type"].GetValue<string>() with _ -> "") with
            | "move_accepted" ->
                let role = o.["role"].GetValue<string>()
                match posIndex.TryFind(o.["move"].GetValue<string>()) with
                | Some idx -> moves <- (role, idx) :: moves
                | None -> ()
            | "game_over" -> outcome <- o.["outcome"].GetValue<string>()
            | _ -> ()
    List.rev moves, outcome

/// Replay the moves, scoring each against the solver. Returns (X tally, O tally, dataAnomaly).
let private score (moves: (string * int) list) : Tally * Tally * bool =
    let board = Array.zeroCreate 9
    let tx, to' = tally (), tally ()
    let mutable anomaly = false
    let mutable stop = false
    for (role, cell) in moves do          // bounded by move list length (<= log length)
        if not stop then
            let p = if role = "X" then 1 else 2
            let t = if role = "X" then tx else to'
            if board.[cell] <> 0 then anomaly <- true; stop <- true     // inconsistent log — stop scoring
            else
                let wins = winningCells board p
                let threats = winningCells board (3 - p)
                let optimal = optimalCells board p
                t.moves <- t.moves + 1
                if not wins.IsEmpty && not (wins.Contains cell) then t.missedWin <- t.missedWin + 1
                elif wins.IsEmpty && not threats.IsEmpty && not (threats.Contains cell) then t.missedBlock <- t.missedBlock + 1
                if not (optimal.Contains cell) then t.suboptimal <- t.suboptimal + 1
                board.[cell] <- p
    tx, to', anomaly

let private roleNode (t: Tally) =
    let o = JsonObject()
    o.["moves"] <- JsonValue.Create t.moves
    o.["missedWin"] <- JsonValue.Create t.missedWin
    o.["missedBlock"] <- JsonValue.Create t.missedBlock
    o.["suboptimal"] <- JsonValue.Create t.suboptimal
    o.["clean"] <- JsonValue.Create(t.missedWin = 0 && t.missedBlock = 0)   // no blunder — strong play, win or draw
    o

let run (argv: string[]) : int =
    let valOf name dflt =
        match Array.tryFindIndex ((=) name) argv with
        | Some i when i + 1 < argv.Length -> argv.[i + 1]
        | _ -> dflt
    let logPath = valOf "--log" null
    let wantGame = match valOf "--game" null with | null -> None | g -> Some g
    let out = valOf "--out" null
    if isNull logPath then eprintfn "quality: --log <request-log.jsonl> required"; 2
    elif not (File.Exists logPath) then eprintfn "quality: log not found: %s" logPath; 2
    else
        let moves, outcome = readGame logPath wantGame
        let tx, to', anomaly = score moves
        let o = JsonObject()
        o.["log"] <- JsonValue.Create(Path.GetFileName logPath)
        o.["outcome"] <- JsonValue.Create outcome   // descriptive only; quality lives in byRole.clean/blunders
        o.["moveCount"] <- JsonValue.Create(List.length moves)
        o.["dataAnomaly"] <- JsonValue.Create anomaly
        let byRole = JsonObject()
        byRole.["X"] <- roleNode tx
        byRole.["O"] <- roleNode to'
        o.["byRole"] <- byRole
        let s = o.ToJsonString(JsonSerializerOptions(WriteIndented = true))
        if not (isNull out) then File.WriteAllText(out, s + "\n")
        printfn "%s" s
        0
