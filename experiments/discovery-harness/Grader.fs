module TicTacToe.DiscoveryHarness.Grader

open TicTacToe.DiscoveryHarness.Transcript

type RecognizeScore =
    { AppIsHit: bool; GoalHit: bool; MultiplayerHit: bool
      RoleNamed: bool; RoleDiscriminationCorrect: bool; FirstActionCoherent: bool }

type Scores =
    { Recognize: RecognizeScore
      AcceptedMoves: int; RejectedMoves: int; RejectionCodes: string list
      Outcome: string; MovesToTerminal: int; Blunders: int; MovesScored: int; Actions: int; Tokens: int }

let private appKw = [| "tic-tac-toe"; "tic tac toe"; "tictactoe"; "noughts" |]
let private goalKw = [| "three in a row"; "3 in a row"; "win"; "row"; "line" |]
// MultiplayerHit is graded from FREE TEXT (the frozen instruction no longer names a
// multiplayer field — see ColdStart). The agent must surface multi-party-ness unprompted.
let private multiplayerKw = [| "two player"; "two-player"; "multiplayer"; "multi-player"; "opponent"; "two players"; "other player"; "against" |]

let private hits (kws: string[]) (text: string) =
    let low = text.ToLowerInvariant()
    kws |> Array.exists (fun k -> low.Contains(k.ToLowerInvariant()))

let private roleNamed (r: RoleReport) =
    [ "x"; "o"; "observer"; "spectator"; "watcher" ] |> List.exists (r.MyRole.ToLowerInvariant().Contains)

let private isObserverSeat (seat: string) = seat.ToLowerInvariant().Contains "observ"

// Match the Simple app's rendered rejection PROSE (Handlers.fs), not internal tokens —
// BodySnippet carries the HTML the agent actually saw. Falls back to the HTTP status.
let private codeOf (snippet: string) (status: int) =
    let low = snippet.ToLowerInvariant()
    if low.Contains "not your turn" then "NotYourTurn"
    elif low.Contains "not a player" then "NotAPlayer"
    elif low.Contains "already taken" then "PositionTaken"
    elif low.Contains "invalid move" then "InvalidMove"
    elif low.Contains "already over" then "GameOver"
    else string status

let private recognize (t: Transcript) : RecognizeScore =
    let appIs, goal, mp =
        match t.Discovery with
        | Some d -> hits appKw d.AppIs, hits goalKw d.Goal,
                    hits multiplayerKw (d.AppIs + " " + d.Goal + " " + d.HowToParticipate)
        | None -> false, false, false
    let named, discrim =
        match t.Role with
        | Some r -> roleNamed r, (r.CanIAct = Some(not (isObserverSeat t.Seat)))
        | None -> false, false
    let firstCoherent =
        match t.Requests |> Seq.tryFind (fun r -> r.Method = "POST") with
        | Some p -> p.Status <> 404
        | None -> false
    { AppIsHit = appIs; GoalHit = goal; MultiplayerHit = mp
      RoleNamed = named; RoleDiscriminationCorrect = discrim; FirstActionCoherent = firstCoherent }

let private quality (t: Transcript) : int * int =
    let mutable blunders, scored = 0, 0
    for idx in 0 .. t.Requests.Count - 1 do
        let r = t.Requests.[idx]
        if r.Method = "POST" && r.Status < 400 then
            let prior =
                t.Boards |> Seq.filter (fun b -> b.AfterRequestIndex < idx)
                         |> Seq.sortByDescending (fun b -> b.AfterRequestIndex) |> Seq.tryHead
            let posName =
                r.Body |> Option.bind (fun b ->
                    b.Split('&') |> Array.tryPick (fun kv ->
                        let p = kv.Split('=') in if p.Length = 2 && p.[0] = "position" then Some p.[1] else None))
            match prior, posName with
            | Some board, Some name when board.Cells.Length = 9 && (t.Seat = "X" || t.Seat = "O") ->
                let chosen = System.Array.IndexOf(HtmlBoard.positions, name)
                if chosen >= 0 then
                    scored <- scored + 1
                    if Optimal.isBlunder board.Cells t.Seat chosen then blunders <- blunders + 1
            | _ -> ()
    blunders, scored

let grade (t: Transcript) : Scores =
    let posts = t.Requests |> Seq.filter (fun r -> r.Method = "POST") |> Seq.toList
    let accepted = posts |> List.filter (fun r -> r.Status < 400)
    let rejected = posts |> List.filter (fun r -> r.Status >= 400)
    let blunders, scored = quality t
    { Recognize = recognize t
      AcceptedMoves = List.length accepted; RejectedMoves = List.length rejected
      RejectionCodes = rejected |> List.map (fun r -> codeOf r.BodySnippet r.Status) |> List.distinct
      Outcome = t.Outcome; MovesToTerminal = List.length accepted
      Blunders = blunders; MovesScored = scored; Actions = t.Actions; Tokens = t.Tokens }
