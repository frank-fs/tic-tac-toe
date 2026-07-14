module SurfaceCellTests

// The primary app IS the 16-cell A/C/Sd/So factorial surface (spec 003b). These are the unit-level
// guards on the toggle itself and on what each factor puts in (or keeps out of) the representation.
// The wire-level guards (422/403/ETag/Link/conneg) live in WireSemanticsTests; cross-app fidelity
// against the retired Surface twin lives in test-equivalence.sh.

open System.Collections.Generic
open Expecto
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web
open TicTacToe.Web.Model
open TicTacToe.Web.templates.game

let private gameId = "11111111-1111-1111-1111-111111111111"

let private boardWith (moves: (SquarePosition * Player) list) =
    let d = Dictionary<SquarePosition, SquareState>()
    for p in [ TopLeft; TopCenter; TopRight; MiddleLeft; MiddleCenter; MiddleRight; BottomLeft; BottomCenter; BottomRight ] do
        d.Add(p, Empty)
    for (pos, player) in moves do
        d.[pos] <- Taken player
    d :> IReadOnlyDictionary<_, _>

/// X to move, with `moves` already played. validMoves = the empty squares.
let private xTurn (moves: (SquarePosition * Player) list) =
    let state = boardWith moves
    let valid =
        state
        |> Seq.filter (fun kv -> kv.Value = Empty)
        |> Seq.map (fun kv -> XPos kv.Key)
        |> Array.ofSeq
    XTurn(state, valid)

let private render (cell: string) userId assignment result =
    renderGameBoard (Surface.parse cell) "/games" gameId result userId assignment 1
    |> Render.toString

/// Count the MOVE forms — the affordance an HTTP agent can actually act on. (The page also
/// carries the reset/delete control forms while the game is in progress; they are not moves.)
let private moveFormCount (html: string) =
    System.Text.RegularExpressions.Regex.Matches(html, "rel=\"make-move\"").Count

[<Tests>]
let tests =
    testList
        "Surface cell (A/C/Sd/So toggle)"
        [ testList
            "cell parsing"
            [ testCase "unset TICTACTOE_CELL is the FULL surface — the app ships complete"
              <| fun _ ->
                  Expect.equal Surface.full { A = true; C = true; Sd = true; So = true } "full = 1111"

              testCase "bit order is A,C,Sd,So (MSB->LSB)"
              <| fun _ ->
                  Expect.equal (Surface.parse "1010") { A = true; C = false; Sd = true; So = false } "1010 = A,Sd"
                  Expect.equal (Surface.parse "0101") { A = false; C = true; Sd = false; So = true } "0101 = C,So"
                  Expect.equal (Surface.parse "0000") Surface.floor "0000 = floor"

              testCase "a malformed cell fails fast — never boot a misconfigured surface"
              <| fun _ ->
                  Expect.throws (fun () -> Surface.parse "101" |> ignore) "wrong length must throw"
                  Expect.throws (fun () -> Surface.parse "10x0" |> ignore) "non-binary char must throw" ]

          testList
            "A — affordance GATING (not presence): the banked instrument"
            [ testCase "A=0 puts a live form on ALL NINE squares, occupied ones included"
              <| fun _ ->
                  // Naive design: nothing is gated. The `disabled` on an illegal square is a
                  // browser-only guard that an HTTP agent ignores — it sees nine submittable forms.
                  let html = render "0000" "someone-else" None (xTurn [ TopLeft, X; TopCenter, O ])
                  Expect.equal (moveFormCount html) 9 "A=0: nine move forms regardless of legality"

              testCase "A=1 gives the active player forms ONLY on their legal squares"
              <| fun _ ->
                  let result = xTurn [ TopLeft, X; TopCenter, O ]
                  let html = render "1000" "" None result
                  Expect.equal (moveFormCount html) 7 "A=1: 7 empty squares -> 7 forms; the 2 taken ones are plain cells"

              testCase "A=1 gives a NON-active viewer no move form at all"
              <| fun _ ->
                  // Both seats filled, X to move, viewer is O -> gated out.
                  let assignment = Some { GameId = gameId; PlayerXId = Some "x-user"; PlayerOId = Some "o-user" }
                  let html = render "1000" "o-user" assignment (xTurn [ TopLeft, X; TopCenter, O ])
                  Expect.equal (moveFormCount html) 0 "A=1: no legal move -> no affordance"

              testCase "A=0 gives that same non-active viewer all nine anyway"
              <| fun _ ->
                  let assignment = Some { GameId = gameId; PlayerXId = Some "x-user"; PlayerOId = Some "o-user" }
                  let html = render "0000" "o-user" assignment (xTurn [ TopLeft, X; TopCenter, O ])
                  Expect.equal (moveFormCount html) 9 "A=0: ungated — this is what discriminates the factor"

              testCase "A=1 self-seat: an UNSEATED visitor on X's turn is offered X's moves"
              <| fun _ ->
                  // The seat-by-first-move bootstrap. Without it an A=1 agent could never start.
                  let html = render "1000" "newcomer" None (xTurn [])
                  Expect.equal (moveFormCount html) 9 "an empty board has 9 legal moves for the claimable seat"
                  Expect.stringContains html "name=\"player\" value=\"X\"" "the form submits as X" ]

          testList
            "C — accessibility structure"
            [ testCase "C=1 exposes roles + the position vocabulary to the a11y tree"
              <| fun _ ->
                  let html = render "0100" "" None (xTurn [ TopLeft, X ])
                  Expect.stringContains html "role=\"grid\"" "the board is a grid"
                  Expect.stringContains html "role=\"gridcell\"" "squares are gridcells"
                  Expect.stringContains html "aria-label=\"TopLeft, X\"" "a taken square announces position + occupancy"
                  Expect.stringContains html "aria-label=\"TopCenter, empty\"" "an empty square announces position + empty"
                  Expect.stringContains html "role=\"status\"" "turn/outcome is a live region"

              testCase "C=0 strips EVERY aria-* and role= from the representation"
              <| fun _ ->
                  // Any leak here silently contaminates the C factor.
                  for cell in [ "0000"; "1000"; "0010"; "0001"; "1011" ] do
                      let html = render cell "" None (xTurn [ TopLeft, X ])
                      Expect.isFalse (html.Contains "aria-") $"cell {cell} must carry no aria-* attribute"
                      Expect.isFalse (html.Contains "role=\"") $"cell {cell} must carry no role= attribute" ]

          testList
            "post-game gate"
            [ testCase "a finished game offers no reset/delete control"
              <| fun _ ->
                  // Otherwise an agent can delete-then-recreate and contaminate the run.
                  let html = render "1111" "" None (Won(boardWith [ TopLeft, X; TopCenter, X; TopRight, X ], X))
                  Expect.isFalse (html.Contains "reset-game-btn") "no reset control on a terminal game"
                  Expect.isFalse (html.Contains "delete-game-btn") "no delete control on a terminal game"

              testCase "an in-progress game always offers both controls as real forms"
              <| fun _ ->
                  let html = render "1111" "" None (xTurn [])
                  Expect.stringContains html "rel=\"reset-game\"" "reset is a real form"
                  Expect.stringContains html "rel=\"delete-game\"" "delete is a real form" ]

          testList
            "route aliasing"
            [ testCase "the representation links and forms under the name it was served as"
              <| fun _ ->
                  let arenas =
                      renderGameBoard Surface.full "/arenas" gameId (xTurn []) "" None 1 |> Render.toString
                  Expect.stringContains arenas $"action=\"/arenas/{gameId}\"" "move forms post to /arenas"
                  Expect.stringContains arenas $"href=\"/games/{gameId}\"" "and advertise the /games alias"
                  let games = render "1111" "" None (xTurn [])
                  Expect.stringContains games $"action=\"/games/{gameId}\"" "move forms post to /games"
                  Expect.stringContains games $"href=\"/arenas/{gameId}\"" "and advertise the /arenas alias" ] ]
