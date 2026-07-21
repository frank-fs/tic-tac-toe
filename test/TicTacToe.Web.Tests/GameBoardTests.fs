module GameBoardTests

open System
open System.Collections.Generic
open Expecto
open Oxpecker.ViewEngine
open TicTacToe.Web.templates.game
open TicTacToe.Model
open TicTacToe.Web.Model
open TicTacToe.Web
// Helper to create a game state with specific moves
let createGameStateWith (moves: (SquarePosition * Player) list) =
    let mutable gameState = Map.empty<SquarePosition, SquareState>

    for (pos, player) in moves do
        gameState <- gameState.Add(pos, Taken player)

    let dict = Dictionary<SquarePosition, SquareState>()

    for kvp in gameState do
        dict.Add(kvp.Key, kvp.Value)

    dict :> IReadOnlyDictionary<SquarePosition, SquareState>

// Helper to create MoveResults for testing
let createXTurnResult gameState validMoves =
    XTurn(gameState, validMoves |> Array.map XPos)

let createOTurnResult gameState validMoves =
    OTurn(gameState, validMoves |> Array.map OPos)

let createWonResult gameState winner = Won(gameState, winner)
let createDrawResult gameState = Draw(gameState)
let createErrorResult gameState message = Error(gameState, message)

let testGameId = "test-game-123"

/// Render as an unassigned viewer who can play (derives player from whose turn it is)
let renderGameBoardToString result =
    let element = renderGameBoard Surface.full "/games" testGameId result "" None 6
    Render.toString element

/// Render as a spectator (no specific viewer)
let renderBroadcastToString result assignment =
    let element = renderGameBoard Surface.full "/games" testGameId result "" assignment 6
    Render.toString element

[<Tests>]
let tests =
    testList
        "Game Board Rendering Tests"
        [
          testCase "Empty board renders correctly for X turn"
          <| fun _ ->
              let emptyState = createGameStateWith []

              let allPositions =
                  [| TopLeft; TopCenter; TopRight
                     MiddleLeft; MiddleCenter; MiddleRight
                     BottomLeft; BottomCenter; BottomRight |]

              let result = createXTurnResult emptyState allPositions
              let html = renderGameBoardToString result

              Expect.stringContains html "X&#39;s turn" "Should show X's turn message"
              Expect.stringContains html "class=\"status\"" "Should contain status div"
              Expect.stringContains html "class=\"board\"" "Should contain board div"
              Expect.stringContains html "class=\"controls\"" "Should contain controls div"
              Expect.stringContains html "square-clickable" "Should have clickable squares for X"

          testCase "Empty board renders correctly for O turn"
          <| fun _ ->
              let emptyState = createGameStateWith []

              let allPositions =
                  [| TopLeft; TopCenter; TopRight
                     MiddleLeft; MiddleCenter; MiddleRight
                     BottomLeft; BottomCenter; BottomRight |]

              let result = createOTurnResult emptyState allPositions
              let html = renderGameBoardToString result

              Expect.stringContains html "O&#39;s turn" "Should show O's turn message"
              Expect.stringContains html "square-clickable" "Should have clickable squares for O"

          testCase "Partially filled board renders correctly"
          <| fun _ ->
              let gameState =
                  createGameStateWith [ (TopLeft, X); (TopCenter, O); (MiddleCenter, X) ]

              let remainingMoves =
                  [| TopRight; MiddleLeft; MiddleRight; BottomLeft; BottomCenter; BottomRight |]

              let result = createOTurnResult gameState remainingMoves
              let html = renderGameBoardToString result

              Expect.stringContains html "O&#39;s turn" "Should show O's turn"
              Expect.stringContains html "<span class=\"player\">X</span>" "Should show X in taken squares"
              Expect.stringContains html "<span class=\"player\">O</span>" "Should show O in taken squares"
              Expect.stringContains html "square-clickable" "Should have clickable squares for remaining moves"

          testCase "Won game renders correctly with X winner"
          <| fun _ ->
              let gameState =
                  createGameStateWith
                      [ (TopLeft, X); (TopCenter, X); (TopRight, X)
                        (MiddleLeft, O); (MiddleCenter, O) ]

              let result = createWonResult gameState X
              let html = renderGameBoardToString result

              Expect.stringContains html "X wins!" "Should show X win message"
              // Post-game gate: a terminal game offers NO reset/delete controls, so an agent cannot
              // delete-then-recreate and contaminate a run with a second game's moves.
              Expect.isFalse (html.Contains "delete-game-btn") "A finished game must offer no delete control"
              Expect.isFalse (html.Contains "reset-game-btn") "A finished game must offer no reset control"
              // The line this replaced ("Delete button should be disabled...") asserted
              // Contains "disabled" and passed, but this scenario has NO delete button at all
              // (asserted directly above) -- it was only ever matching the square buttons' HTML
              // `disabled` attribute, a stale/copy-pasted assertion that never tested what its
              // own message claimed. Surfaced by removing `disabled` from the squares (a
              // deliberate accessibility fix -- native `disabled` strips an element from the
              // accessibility tree regardless of role/aria-label), which made the false pass
              // visible. No delete-button-disabled behavior to test here; removed rather than
              // reworded onto a still-wrong premise.
              Expect.isFalse (html.Contains("square-clickable")) "Should not have any clickable squares"

          testCase "Draw game renders correctly"
          <| fun _ ->
              let gameState =
                  createGameStateWith
                      [ (TopLeft, X); (TopCenter, O); (TopRight, X)
                        (MiddleLeft, O); (MiddleCenter, X); (MiddleRight, O)
                        (BottomLeft, O); (BottomCenter, X); (BottomRight, O) ]

              let result = createDrawResult gameState
              let html = renderGameBoardToString result

              Expect.stringContains html "It&#39;s a draw!" "Should show draw message"
              Expect.isFalse (html.Contains "delete-game-btn") "A finished game must offer no delete control"
              Expect.isFalse (html.Contains("square-clickable")) "Should not have any clickable squares"

          testCase "Error state renders correctly"
          <| fun _ ->
              let gameState = createGameStateWith [ (TopLeft, X) ]
              let errorMessage = "Invalid move attempted"
              let result = createErrorResult gameState errorMessage
              let html = renderGameBoardToString result

              Expect.stringContains html $"Error: {errorMessage}" "Should show error message"
              Expect.isFalse (html.Contains("square-clickable")) "Should not have any clickable squares"

          testCase "Game board renders all 9 squares"
          <| fun _ ->
              let emptyState = createGameStateWith []

              let allPositions =
                  [| TopLeft; TopCenter; TopRight
                     MiddleLeft; MiddleCenter; MiddleRight
                     BottomLeft; BottomCenter; BottomRight |]

              let result = createXTurnResult emptyState allPositions
              let html = renderGameBoardToString result

              let squareMatches =
                  System.Text.RegularExpressions.Regex.Matches(html, "class=\"[^\"]*square[^\"]*\"")

              Expect.equal squareMatches.Count 9 "Should render exactly 9 squares"

          testCase "Clickable squares have correct data attributes for move endpoint"
          <| fun _ ->
              let gameState = createGameStateWith [ (TopLeft, X) ]
              let remainingMoves = [| TopCenter; TopRight |]
              let result = createOTurnResult gameState remainingMoves
              let html = renderGameBoardToString result

              Expect.stringContains html $"@post(&#39;/games/{testGameId}&#39;)" "Should have POST to game resource"
              Expect.stringContains html "$player = &#39;O&#39;" "Should set player signal in click handler"
              Expect.stringContains html "$position = &#39;TopCenter&#39;" "Should set position signal in click handler"

          testCase "Board shows correct turn alternation"
          <| fun _ ->
              let gameProgression =
                  [ ([], X)
                    ([ (TopLeft, X) ], O)
                    ([ (TopLeft, X); (TopCenter, O) ], X)
                    ([ (TopLeft, X); (TopCenter, O); (MiddleLeft, X) ], O) ]

              for (i, (moves, expectedPlayer)) in gameProgression |> List.indexed do
                  let gameState = createGameStateWith moves

                  let validMoves =
                      [| TopRight; MiddleCenter; MiddleRight; BottomLeft; BottomCenter; BottomRight |]
                      |> Array.filter (fun pos -> moves |> List.exists (fun (p, _) -> p = pos) |> not)

                  let result =
                      match expectedPlayer with
                      | X -> createXTurnResult gameState validMoves
                      | O -> createOTurnResult gameState validMoves

                  let html = renderGameBoardToString result
                  let expectedMessage = $"{expectedPlayer}&#39;s turn"

                  Expect.stringContains html expectedMessage $"Should show {expectedPlayer}'s turn for move {i}"

          testCase "Legend shows waiting for both players when no assignment"
          <| fun _ ->
              let emptyState = createGameStateWith []

              let allPositions =
                  [| TopLeft; TopCenter; TopRight
                     MiddleLeft; MiddleCenter; MiddleRight
                     BottomLeft; BottomCenter; BottomRight |]

              let result = createXTurnResult emptyState allPositions
              let html = renderBroadcastToString result None

              Expect.stringContains html "class=\"legend\"" "Should contain legend div"
              Expect.stringContains html "X: Waiting for player..." "Should show waiting for player X"
              Expect.stringContains html "O: Waiting for player..." "Should show waiting for player O"

          testCase "Legend shows player X ID and waiting for player O"
          <| fun _ ->
              let emptyState = createGameStateWith []

              let allPositions =
                  [| TopLeft; TopCenter; TopRight
                     MiddleLeft; MiddleCenter; MiddleRight
                     BottomLeft; BottomCenter; BottomRight |]

              let result = createXTurnResult emptyState allPositions
              let assignment = Some { GameId = testGameId; PlayerXId = Some "abcdef12-3456-7890-abcd-ef1234567890"; PlayerOId = None }
              let html = renderBroadcastToString result assignment

              Expect.stringContains html "X: abcdef12" "Should show first 8 chars of player X ID"
              Expect.stringContains html "O: Waiting for player..." "Should show waiting for player O"

          testCase "Legend shows both player IDs when both assigned"
          <| fun _ ->
              let emptyState = createGameStateWith []

              let allPositions =
                  [| TopLeft; TopCenter; TopRight
                     MiddleLeft; MiddleCenter; MiddleRight
                     BottomLeft; BottomCenter; BottomRight |]

              let result = createXTurnResult emptyState allPositions
              let assignment = Some { GameId = testGameId; PlayerXId = Some "abcdef12-3456-7890-abcd-ef1234567890"; PlayerOId = Some "99887766-5544-3322-1100-aabbccddeeff" }
              let html = renderBroadcastToString result assignment

              Expect.stringContains html "X: abcdef12" "Should show first 8 chars of player X ID"
              Expect.stringContains html "O: 99887766" "Should show first 8 chars of player O ID"

          testCase "Legend highlights active player O when it is O's turn"
          <| fun _ ->
              let gameState = createGameStateWith [ (TopLeft, X) ]
              let remainingMoves = [| TopCenter; TopRight; MiddleLeft; MiddleCenter; MiddleRight; BottomLeft; BottomCenter; BottomRight |]
              let result = createOTurnResult gameState remainingMoves
              let assignment = Some { GameId = testGameId; PlayerXId = Some "abcdef12-3456-7890-abcd-ef1234567890"; PlayerOId = Some "99887766-5544-3322-1100-aabbccddeeff" }
              let html = renderBroadcastToString result assignment

              Expect.stringContains html "class=\"legend-active\"" "Should have legend-active class for active player"
              // O's span should have the active class since it's O's turn
              Expect.stringContains html "<span class=\"legend-active\">O:" "O legend entry should be active"
              Expect.isFalse (html.Contains("<span class=\"legend-active\">X:")) "X legend entry should not be active"

          testCase "Legend highlights active player X when it is X's turn"
          <| fun _ ->
              let emptyState = createGameStateWith []
              let allPositions = [| TopLeft; TopCenter; TopRight; MiddleLeft; MiddleCenter; MiddleRight; BottomLeft; BottomCenter; BottomRight |]
              let result = createXTurnResult emptyState allPositions
              let assignment = Some { GameId = testGameId; PlayerXId = Some "abcdef12-3456-7890-abcd-ef1234567890"; PlayerOId = Some "99887766-5544-3322-1100-aabbccddeeff" }
              let html = renderBroadcastToString result assignment

              Expect.stringContains html "<span class=\"legend-active\">X:" "X legend entry should be active"
              Expect.isFalse (html.Contains("<span class=\"legend-active\">O:")) "O legend entry should not be active"

          testCase "Legend has no active player when game is over"
          <| fun _ ->
              let gameState =
                  createGameStateWith
                      [ (TopLeft, X); (TopCenter, X); (TopRight, X)
                        (MiddleLeft, O); (MiddleCenter, O) ]
              let result = createWonResult gameState X
              let assignment = Some { GameId = testGameId; PlayerXId = Some "abcdef12-3456-7890-abcd-ef1234567890"; PlayerOId = Some "99887766-5544-3322-1100-aabbccddeeff" }
              let html = renderBroadcastToString result assignment

              Expect.isFalse (html.Contains("legend-active")) "Should not have legend-active class when game is over"

          testCase "Reset game renders legend with waiting for both players"
          <| fun _ ->
              // After reset, game is fresh: XTurn with no assignment
              let emptyState = createGameStateWith []
              let allPositions = [| TopLeft; TopCenter; TopRight; MiddleLeft; MiddleCenter; MiddleRight; BottomLeft; BottomCenter; BottomRight |]
              let result = createXTurnResult emptyState allPositions
              let html = renderBroadcastToString result None

              Expect.stringContains html "class=\"legend\"" "Reset game should contain legend div"
              Expect.stringContains html "X: Waiting for player..." "Reset game should show waiting for player X"
              Expect.stringContains html "O: Waiting for player..." "Reset game should show waiting for player O"

          testCase "Game board div contains legend for SSE broadcast"
          <| fun _ ->
              // SSE initial-connect uses renderGameBoardForBroadcast - verify legend is in the game-board div
              let gameState = createGameStateWith [ (TopLeft, X) ]
              let remainingMoves = [| TopCenter; TopRight; MiddleLeft; MiddleCenter; MiddleRight; BottomLeft; BottomCenter; BottomRight |]
              let result = createOTurnResult gameState remainingMoves
              let assignment = Some { GameId = testGameId; PlayerXId = Some "abcdef12-3456-7890-abcd-ef1234567890"; PlayerOId = None }
              let html = renderBroadcastToString result assignment

              // Verify legend is inside the game-board div (between board and controls)
              let legendIdx = html.IndexOf("class=\"legend\"")
              let boardIdx = html.IndexOf("class=\"board\"")
              let controlsIdx = html.IndexOf("class=\"controls\"")
              Expect.isGreaterThan legendIdx boardIdx "Legend should come after board"
              Expect.isLessThan legendIdx controlsIdx "Legend should come before controls"
              Expect.stringContains html "X: abcdef12" "SSE broadcast should include player X ID in legend"

          testCase "Empty clickable cells are named by action+position; preview stays aria-hidden"
          <| fun _ ->
              // Active X player, one X placed. The move-preview "X" in empty cells is
              // decorative — it must be aria-hidden so the accessibility tree (which the
              // discovery agents read) shows exactly one placed X, not nine. Empty cells'
              // affordance names the location, its emptiness, and the claim action ("<pos>
              // square, empty, claim it for X"), so the control is unambiguous in the
              // accessibility tree.
              let gameState = createGameStateWith [ (TopLeft, X) ]
              let remainingMoves =
                  [| TopCenter; TopRight; MiddleLeft; MiddleCenter
                     MiddleRight; BottomLeft; BottomCenter; BottomRight |]
              let result = createXTurnResult gameState remainingMoves
              let html = renderGameBoardToString result

              Expect.stringContains html "<span class=\"player\">X</span>" "Taken cell shows the placed X"
              Expect.isFalse (html.Contains("<span class=\"preview\">")) "Preview span must not be bare — it must carry aria-hidden"
              Expect.stringContains html "<span class=\"preview\" aria-hidden=\"true\">X</span>" "Move-preview is decorative and hidden from a11y tree"
              Expect.stringContains html "aria-label=\"top center square, empty, claim it for X\"" "Empty clickable cell names the location, emptiness, and claim action"
              Expect.stringContains html "aria-label=\"middle center square, empty, claim it for X\"" "Empty clickable cell names the location, emptiness, and claim action" ]
