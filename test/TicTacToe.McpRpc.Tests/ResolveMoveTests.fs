module TicTacToe.McpRpc.Tests.ResolveMoveTests

open Expecto
open TicTacToe.Engine
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
