module TicTacToe.Engine.Tests.SupervisorTests

open Expecto
open TicTacToe.Engine
open TicTacToe.Model

[<Tests>]
let supervisorTests =
    testList
        "GameSupervisor Tests"
        [ testCaseAsync "Can create and track multiple concurrent games"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  // Create two games
                  let (gameId1, game1) = supervisor.CreateGame()
                  let (gameId2, game2) = supervisor.CreateGame()

                  Expect.equal (supervisor.GetActiveGameCount()) 2 "Should track 2 active games"

                  // Make sure we can retrieve both games
                  Expect.isSome (supervisor.GetGame(gameId1)) "Should find game1"
                  Expect.isSome (supervisor.GetGame(gameId2)) "Should find game2"

                  // Games should be different instances
                  Expect.notEqual game1 game2 "Games should be different instances"

              finally
                  supervisor.Dispose()
          }

          testCaseAsync "Game completes and disposes correctly"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  let (gameId, game) = supervisor.CreateGame()

                  // Complete the game
                  game.MakeMove(XMove TopLeft)
                  game.MakeMove(OMove TopCenter)
                  game.MakeMove(XMove MiddleLeft)
                  game.MakeMove(OMove TopRight)
                  game.MakeMove(XMove BottomLeft) // X wins

              finally
                  supervisor.Dispose()
          }

          testCaseAsync "Games are cleaned up when completed"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  let (gameId, game) = supervisor.CreateGame()
                  Expect.equal (supervisor.GetActiveGameCount()) 1 "Should have 1 active game"

                  // Complete the game quickly
                  game.MakeMove(XMove TopLeft)
                  game.MakeMove(OMove TopCenter)
                  game.MakeMove(XMove MiddleLeft)
                  game.MakeMove(OMove TopRight)
                  game.MakeMove(XMove BottomLeft) // X wins

                  // Allow more time for async cleanup to complete
                  do! Async.Sleep(100)

                  // Game should be cleaned up
                  Expect.equal (supervisor.GetActiveGameCount()) 0 "Game should be cleaned up"
                  Expect.isNone (supervisor.GetGame(gameId)) "Game should no longer be retrievable"

              finally
                  supervisor.Dispose()
          }

          testCaseAsync "Game properly validates move conflicts and turn order"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  let (gameId, game) = supervisor.CreateGame()

                  let resultList = System.Collections.Generic.List<MoveResult>()

                  use subscription =
                      game.Subscribe(
                          { new System.IObserver<MoveResult> with
                              member _.OnNext(result) = resultList.Add(result)
                              member _.OnError(ex) = () // System errors only
                              member _.OnCompleted() = () }
                      )

                  // Make X's first move
                  game.MakeMove(XMove TopLeft)
                  do! Async.Sleep(50) // Allow processing

                  // Try to make another X move - should generate Error MoveResult
                  game.MakeMove(XMove TopCenter)
                  do! Async.Sleep(50) // Allow processing

                  // Should have received Error MoveResults by now
                  let errorResults =
                      resultList
                      |> Seq.filter (fun r ->
                          match r with
                          | Error _ -> true
                          | _ -> false)
                      |> Seq.toList

                  Expect.isGreaterThan errorResults.Length 0 "Should have received error for wrong turn"

                  let firstError = errorResults.[0]

                  let errorMessage =
                      match firstError with
                      | Error(_, msg) -> msg
                      | _ -> ""

                  Expect.stringContains errorMessage "Invalid" "Should reject wrong turn move"

                  // Try to make O move to same square as X - should also generate error
                  game.MakeMove(OMove TopLeft)
                  do! Async.Sleep(50) // Allow processing

                  // Should have another error
                  let allErrors =
                      resultList
                      |> Seq.filter (fun r ->
                          match r with
                          | Error _ -> true
                          | _ -> false)
                      |> Seq.toList

                  Expect.isGreaterThanOrEqual allErrors.Length 2 "Should have errors for both invalid moves"

                  // Make valid O move - should succeed
                  game.MakeMove(OMove TopCenter)
                  do! Async.Sleep(50) // Allow processing

                  // Should have valid results (initial + first X move + valid O move + error results + preserved states)
                  Expect.isGreaterThanOrEqual resultList.Count 5 "Should have valid move results and error results"

              finally
                  supervisor.Dispose()
          }

          testCaseAsync "TryCreateGame creates below the cap and refuses atomically at the cap"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  Expect.isSome (supervisor.TryCreateGame(Some 2)) "First create is below the cap"
                  Expect.isSome (supervisor.TryCreateGame(Some 2)) "Second create reaches the cap"
                  Expect.isNone (supervisor.TryCreateGame(Some 2)) "Third create is refused at the cap"
                  Expect.equal (supervisor.GetActiveGameCount()) 2 "Cap must not be exceeded"
              finally
                  supervisor.Dispose()
          }

          testCaseAsync "TryCreateGame with no cap always creates"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  Expect.isSome (supervisor.TryCreateGame(None)) "No cap: always creates"
                  Expect.isSome (supervisor.TryCreateGame(None)) "No cap: always creates"
                  Expect.equal (supervisor.GetActiveGameCount()) 2 "Both games tracked"
              finally
                  supervisor.Dispose()
          }

          testCaseAsync "SnapshotActiveGames returns the seeded initial state (never null)"
          <| async {
              let supervisor = createGameSupervisor ()

              try
                  let (gameId, game) = supervisor.CreateGame()
                  let snapshot = supervisor.SnapshotActiveGames()

                  Expect.equal snapshot.Length 1 "Snapshot has the one created game"
                  let (snapId, snapState) = snapshot.Head
                  Expect.equal snapId gameId "Snapshot carries the game id"
                  Expect.isNotNull (box snapState) "Cached state is seeded synchronously, never null"
                  Expect.equal snapState (game.GetState()) "Cached state matches the live state"

                  match supervisor.TryGetState(gameId) with
                  | Some s -> Expect.equal s (game.GetState()) "TryGetState matches live state"
                  | None -> failtest "TryGetState should find the freshly created game"
              finally
                  supervisor.Dispose()
          } ]
