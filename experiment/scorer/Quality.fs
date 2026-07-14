namespace TicTacToe.Quality

// Play-quality metric for the tic-tac-toe sample (spec 001 §7). Tic-tac-toe is SOLVED: two
// attentive, strategy-aware players draw. But a win is legitimate and the WINNER is not a failure —
// taking a win the opponent hands you is optimal play. The failure is specific and attributable:
// the LOSER's missed block (or missed win), a single player's move, not the game's outcome. So the
// quality axis is per-player blunder-free play, NOT the outcome label (counting wins as the agent's
// goal is what's dishonest — the goal is correct play, whose modal result between equals is a draw).
//
// Score is PURE (R14): it replays the harness-supplied, already-filtered event list for ONE episode
// through an exact 3x3 minimax solver (deterministic, no LLM judge, no I/O) and reports:
//   - Outcome: purely descriptive (draw / x_wins / o_wins / incomplete)
//   - per-role missedWin / missedBlock / suboptimal + Clean (did THIS player blunder?)
// A clean winner (0 blunders, opponent missed a block) is a strong game, not a failure.
//
// Relocated from experiments/oss-driver/Quality.fs; the CLI/log-reading half now belongs to the
// harness (`quality` subcommand), which loads this assembly and calls Score.

open System.Text.Json.Nodes
open AgentHypothesis.Contract

module private Solver =

    /// Board: 9 cells, row-major index 0..8. 0 = empty, 1 = X, 2 = O.
    let posIndex =
        [ "TopLeft", 0; "TopCenter", 1; "TopRight", 2
          "MiddleLeft", 3; "MiddleCenter", 4; "MiddleRight", 5
          "BottomLeft", 6; "BottomCenter", 7; "BottomRight", 8 ]
        |> Map.ofList

    let private lines =
        [ [ 0; 1; 2 ]; [ 3; 4; 5 ]; [ 6; 7; 8 ]
          [ 0; 3; 6 ]; [ 1; 4; 7 ]; [ 2; 5; 8 ]
          [ 0; 4; 8 ]; [ 2; 4; 6 ] ]

    let winner (b: int[]) : int =
        lines
        |> List.tryPick (fun l ->
            match l |> List.map (fun i -> b.[i]) with
            | [ a; c; d ] when a <> 0 && a = c && c = d -> Some a
            | _ -> None)
        |> Option.defaultValue 0

    let legal (b: int[]) = [ for i in 0..8 do if b.[i] = 0 then yield i ]

    /// Empty cells where placing p immediately completes a line (an available win for p).
    let winningCells (b: int[]) (p: int) =
        legal b
        |> List.filter (fun i -> let c = Array.copy b in c.[i] <- p; winner c = p)
        |> Set.ofList

    /// Minimax value of board `b` with `p` to move, from p's perspective: +1 win, 0 draw, -1 loss.
    /// Bounded: recursion depth <= empty-cell count <= 9 (R10).
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
    let optimalCells (b: int[]) (p: int) =
        let vs = legal b |> List.map (fun i -> i, moveValue b p i)
        let best = vs |> List.map snd |> List.max
        vs |> List.filter (fun (_, v) -> v = best) |> List.map fst |> Set.ofList

module private Replay =

    type Tally =
        { mutable moves: int
          mutable missedWin: int
          mutable missedBlock: int
          mutable suboptimal: int }

    let tally () = { moves = 0; missedWin = 0; missedBlock = 0; suboptimal = 0 }

    /// Replay the accepted moves, scoring each against the solver. Returns (X tally, O tally, anomaly).
    /// Bounded by the caller-supplied move list (R10).
    let score (moves: (string * int) list) : Tally * Tally * bool =
        let board = Array.zeroCreate 9
        let tx, to' = tally (), tally ()
        let mutable anomaly = false
        let mutable stop = false
        for (role, cell) in moves do
            if not stop then
                let p = if role = "X" then 1 else 2
                let t = if role = "X" then tx else to'
                if board.[cell] <> 0 then
                    anomaly <- true                     // inconsistent log — stop scoring, flag it
                    stop <- true
                else
                    let wins = Solver.winningCells board p
                    let threats = Solver.winningCells board (3 - p)
                    let optimal = Solver.optimalCells board p
                    t.moves <- t.moves + 1
                    if not wins.IsEmpty && not (wins.Contains cell) then t.missedWin <- t.missedWin + 1
                    elif wins.IsEmpty && not threats.IsEmpty && not (threats.Contains cell) then
                        t.missedBlock <- t.missedBlock + 1
                    if not (optimal.Contains cell) then t.suboptimal <- t.suboptimal + 1
                    board.[cell] <- p
        tx, to', anomaly

    let roleQuality (role: string) (t: Tally) : RoleQuality =
        { Role = role
          Moves = t.moves
          // No attributable blunder — strong play, win or draw. `suboptimal` is reported but does
          // not decide Clean: an optimal-set deviation that concedes nothing is not a blunder.
          Clean = t.missedWin = 0 && t.missedBlock = 0
          Blunders =
            Map.ofList
                [ "missedWin", t.missedWin
                  "missedBlock", t.missedBlock
                  "suboptimal", t.suboptimal ]
          Metrics = Map.empty }

/// The sample's IQualityScorer — the ONE exported implementing type the harness resolves (§7.2).
type MinimaxScorer() =

    /// The wire vocabulary is read by SUFFIX so both the generic §5 names (action_accepted /
    /// episode_over) and this app's historical names (move_accepted / game_over) score identically.
    static let isAccepted (e: WireEvent) = e.EventType.EndsWith "_accepted"
    static let isOver (e: WireEvent) = e.EventType.EndsWith "_over"

    /// The move token lives in the sample-opaque payload — `{ "move": "TopLeft" }`.
    static let moveOf (e: WireEvent) : int option =
        match e.Payload.["move"] with
        | null -> None
        | node -> Solver.posIndex.TryFind(node.GetValue<string>())

    interface IQualityScorer with
        member _.Score(events, _groundTruth: JsonObject) =
            let moves =
                events
                |> List.filter isAccepted
                |> List.choose (fun e ->
                    match e.Actor, moveOf e with
                    | Some actor, Some cell -> Some(actor, cell)
                    | _ -> None)

            let outcome =
                events
                |> List.tryFindBack isOver
                |> Option.bind (fun e -> e.Outcome)
                |> Option.defaultValue "incomplete"

            let tx, to', anomaly = Replay.score moves

            { Outcome = outcome                     // descriptive only; quality lives in ByRole
              MoveCount = List.length moves
              DataAnomaly = anomaly
              ByRole = [ Replay.roleQuality "X" tx; Replay.roleQuality "O" to' ] }
