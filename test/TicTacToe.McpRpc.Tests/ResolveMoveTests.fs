module TicTacToe.McpRpc.Tests.ResolveMoveTests

open Expecto
open TicTacToe.Engine
open TicTacToe.McpRpc
open TicTacToe.McpRpc.Identity

let private freshGame () =
    let sup = createGameSupervisor ()
    let gameId, _ = sup.CreateGame()
    sup, gameId

[<Tests>]
let resolveMoveTests =
    testList
        "resolveMove"
        [ testCase "no token -> unauthenticated"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              let r = resolveMove sup store None gameId "TopLeft"
              Expect.equal r (Rejected "unauthenticated") "missing claim is rejected"

          testCase "unknown game -> game_not_found"
          <| fun _ ->
              let sup, _ = freshGame ()
              let store = PlayerAssignmentStore()
              let r = resolveMove sup store (Some "tokA") "no-such-game" "TopLeft"
              Expect.equal r (Rejected "game_not_found") "unknown gameId rejected"

          testCase "authenticated first move succeeds and binds X"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()

              match resolveMove sup store (Some "tokA") gameId "TopLeft" with
              | Moved(board, whoseTurn, status) ->
                  Expect.equal board.[0] "X" "X placed at TopLeft"
                  Expect.equal whoseTurn "O" "now O's turn"
                  Expect.equal status "in_progress" "game continues"
              | other -> failtestf "expected Moved, got %A" other

          testCase "second token moving out of turn -> not_your_turn"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              resolveMove sup store (Some "tokA") gameId "TopLeft" |> ignore
              let r = resolveMove sup store (Some "tokA") gameId "TopCenter"
              Expect.equal r (Rejected "not_your_turn") "X cannot move on O's turn"

          testCase "occupied square -> position_taken"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              resolveMove sup store (Some "tokA") gameId "TopLeft" |> ignore
              let r = resolveMove sup store (Some "tokB") gameId "TopLeft"
              Expect.equal r (Rejected "position_taken") "cannot replay a taken square"

          testCase "invalid position string -> invalid_input"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              let r = resolveMove sup store (Some "tokA") gameId "Nope"
              Expect.equal r (Rejected "invalid_input") "unparseable position rejected" ]

open System.Security.Claims

let private principal (token: string) =
    ClaimsPrincipal(ClaimsIdentity([ Claim(ClaimTypes.Name, token) ], "Test", ClaimTypes.Name, ClaimTypes.Role))

let private prop (name: string) (o: obj) : obj =
    o.GetType().GetProperty(name).GetValue(o)

[<Tests>]
let toolsTests =
    testList
        "TicTacToeTools"
        [ testCase "authenticate returns a non-empty token"
          <| fun _ ->
              let tools = Tools.TicTacToeTools(createGameSupervisor (), PlayerAssignmentStore())
              let resp = tools.authenticate ()
              Expect.isNotEmpty resp.token "token returned"

          testCase "make_move with no claim is unauthenticated"
          <| fun _ ->
              let sup = createGameSupervisor ()
              let gameId, _ = sup.CreateGame()
              let tools = Tools.TicTacToeTools(sup, PlayerAssignmentStore())
              let resp = tools.make_move (null, gameId, "TopLeft")
              Expect.equal (prop "error" resp :?> string) "unauthenticated" "no claim rejected"

          testCase "make_move with a claim places the mark"
          <| fun _ ->
              let sup = createGameSupervisor ()
              let gameId, _ = sup.CreateGame()
              let tools = Tools.TicTacToeTools(sup, PlayerAssignmentStore())
              let resp = tools.make_move (principal "tokA", gameId, "TopLeft")
              let board = (prop "board" resp) :?> string[]
              Expect.equal board.[0] "X" "X placed via claim identity" ]
