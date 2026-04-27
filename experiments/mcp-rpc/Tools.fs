module TicTacToe.McpRpc.Tools

open System.ComponentModel
open System.Text.Json.Serialization
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Model

// Ordered list of all positions (index 0–8)
let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private supervisor = createGameSupervisor ()

/// Render the board as 9 cells: "X", "O", or "" (empty)
let private renderBoard (gameState: GameState) : string[] =
    allPositions
    |> Array.map (fun pos ->
        match gameState.TryGetValue(pos) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private whoseTurn (result: MoveResult) =
    match result with
    | XTurn _ -> "X"
    | OTurn _ -> "O"
    | Won(_, player) -> sprintf "%O won" player
    | Draw _ -> "draw"
    | Error _ -> "error"

let private statusStr (result: MoveResult) =
    match result with
    | XTurn _ -> "in_progress"
    | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error(_, msg) -> sprintf "error: %s" msg

let private validMoves (result: MoveResult) : string[] =
    match result with
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

// ─── Response record types ───────────────────────────────────────────────────

type NewGameResponse = { gameId: string }

type BoardResponse =
    { board: string[]
      whoseTurn: string
      status: string
      validMoves: string[] }

type MoveResponse =
    { board: string[]
      whoseTurn: string
      status: string }

type ErrorResponse =
    { error: string
      position: string }

type GameStateResponse =
    { gameId: string
      board: string[]
      whoseTurn: string
      status: string
      validMoves: string[] }

// ─── Tool implementations ─────────────────────────────────────────────────────

[<McpServerToolType>]
type TicTacToeTools() =

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns a gameId to use in subsequent calls. X always moves first.")>]
    static member new_game() : NewGameResponse =
        let gameId, _ = supervisor.CreateGame()
        { gameId = gameId }

    [<McpServerTool>]
    [<Description("Get the current board state for a game. Returns the board as 9 cells (index 0=TopLeft to 8=BottomRight), whose turn it is, game status, and valid moves.")>]
    static member get_board
        (
            [<Description("The game ID returned by new_game")>] gameId: string
        ) : obj =
        match supervisor.GetGame(gameId) with
        | None ->
            box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            let result = game.GetState()
            let gs =
                match result with
                | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs
            box
                { board = renderBoard gs
                  whoseTurn = whoseTurn result
                  status = statusStr result
                  validMoves = validMoves result }

    [<McpServerTool>]
    [<Description("Make a move in a tic-tac-toe game. player must be 'X' or 'O'. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight. Returns updated board on success, or a structured error.")>]
    static member make_move
        (
            [<Description("The game ID returned by new_game")>] gameId: string,
            [<Description("The player making the move: 'X' or 'O'")>] player: string,
            [<Description("Board position: TopLeft | TopCenter | TopRight | MiddleLeft | MiddleCenter | MiddleRight | BottomLeft | BottomCenter | BottomRight")>] position: string
        ) : obj =
        match supervisor.GetGame(gameId) with
        | None ->
            box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            match Move.TryParse(player, position) with
            | None ->
                box {| error = "invalid_input"; player = player; position = position |}
            | Some move ->
                let before = game.GetState()
                game.MakeMove(move)
                let after = game.GetState()
                match after with
                | Error(gs, msg) ->
                    // Determine structured error code
                    let code =
                        match msg with
                        | m when m.Contains("already") -> "game_over"
                        | m when m.Contains("Invalid") ->
                            // Distinguish position_taken vs wrong_player by checking if it was a wrong-turn error
                            match before with
                            | XTurn _ when (match move with OMove _ -> true | _ -> false) -> "wrong_player"
                            | OTurn _ when (match move with XMove _ -> true | _ -> false) -> "wrong_player"
                            | _ -> "position_taken"
                        | _ -> "invalid_move"
                    box {| error = code; position = position |}
                | result ->
                    let gs =
                        match result with
                        | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs
                    box
                        { board = renderBoard gs
                          whoseTurn = whoseTurn result
                          status = statusStr result }

    [<McpServerTool>]
    [<Description("Get full game state including board, turn, status, and valid moves for the game.")>]
    static member get_state
        (
            [<Description("The game ID returned by new_game")>] gameId: string
        ) : obj =
        match supervisor.GetGame(gameId) with
        | None ->
            box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            let result = game.GetState()
            let gs =
                match result with
                | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs
            box
                { gameId = gameId
                  board = renderBoard gs
                  whoseTurn = whoseTurn result
                  status = statusStr result
                  validMoves = validMoves result }
