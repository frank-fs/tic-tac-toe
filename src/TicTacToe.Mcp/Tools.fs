module TicTacToe.Mcp.Tools

open System.Collections.Concurrent
open System.ComponentModel
open System.Text.Json
open System.Text.Json.Nodes
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Model

type RejectionReason =
    | GameNotFound
    | InvalidPosition
    | PositionTaken
    | GameOver

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) =
    allPositions
    |> Array.map (fun pos ->
        match gs.TryGetValue(pos) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private getGs = function
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

let private statusStr = function
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error _ -> "error"

let private whoseTurnStr = function
    | XTurn _ -> "X"
    | OTurn _ -> "O"
    | Won _ | Draw _ | Error _ -> "game_over"

let private validMovesArr = function
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

let private stateJson (result: MoveResult) =
    let gs = getGs result
    let obj = JsonObject()
    obj["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
    obj["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
    obj["status"] <- JsonValue.Create(statusStr result)
    obj["validMoves"] <- JsonNode.Parse(JsonSerializer.Serialize(validMovesArr result))
    obj

let private errorJson (reason: RejectionReason) =
    let obj = JsonObject()
    obj["error"] <- JsonValue.Create(reason.ToString())
    obj.ToJsonString()

let private isTerminal = function
    | Won _ | Draw _ -> true
    | _ -> false

[<McpServerToolType>]
type GameTools(supervisor: GameSupervisor) =
    let completedGames = ConcurrentDictionary<string, MoveResult>()

    let resolveGame gameId =
        match supervisor.GetGame(gameId) with
        | Some game -> Some (Choice1Of2 game)
        | None ->
            match completedGames.TryGetValue(gameId) with
            | true, result -> Some (Choice2Of2 result)
            | _ -> None

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns gameId, board (9 cells), whoseTurn, status, and validMoves. X always moves first.")>]
    member _.``new_game``() : string =
        let gameId, _ = supervisor.CreateGame()
        match supervisor.GetGame(gameId) with
        | None -> errorJson GameNotFound
        | Some game ->
            let obj = stateJson (game.GetState())
            obj["gameId"] <- JsonValue.Create(gameId)
            obj.ToJsonString()

    [<McpServerTool>]
    [<Description("Get board state for a game. Returns board, whoseTurn, status, validMoves.")>]
    member _.``get_board``(
        [<Description("Game ID returned by new_game")>] gameId: string) : string =
        match resolveGame gameId with
        | None -> errorJson GameNotFound
        | Some (Choice1Of2 game) -> (stateJson (game.GetState())).ToJsonString()
        | Some (Choice2Of2 result) -> (stateJson result).ToJsonString()

    [<McpServerTool>]
    [<Description("Make a move in the current player's turn. position is one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.``make_move``(
        [<Description("Game ID")>] gameId: string,
        [<Description("Square to claim, e.g. TopLeft")>] position: string) : string =
        match resolveGame gameId with
        | None -> errorJson GameNotFound
        | Some (Choice2Of2 _) -> errorJson GameOver
        | Some (Choice1Of2 game) ->
            let currentResult = game.GetState()
            match currentResult with
            | Won _ | Draw _ ->
                completedGames.TryAdd(gameId, currentResult) |> ignore
                errorJson GameOver
            | _ ->
                match SquarePosition.TryParse(position) with
                | None -> errorJson InvalidPosition
                | Some pos ->
                    let gs = getGs currentResult
                    match gs.TryGetValue(pos) with
                    | true, Taken _ -> errorJson PositionTaken
                    | _ ->
                        let move =
                            match currentResult with
                            | XTurn _ -> XMove pos
                            | _ -> OMove pos
                        game.MakeMove(move)
                        let nextResult = game.GetState()
                        if isTerminal nextResult then
                            completedGames.TryAdd(gameId, nextResult) |> ignore
                        (stateJson nextResult).ToJsonString()

    [<McpServerTool>]
    [<Description("Get full game state including gameId, board, whoseTurn, status, and validMoves.")>]
    member _.``get_state``(
        [<Description("Game ID")>] gameId: string) : string =
        match resolveGame gameId with
        | None -> errorJson GameNotFound
        | Some (Choice1Of2 game) ->
            let obj = stateJson (game.GetState())
            obj["gameId"] <- JsonValue.Create(gameId)
            obj.ToJsonString()
        | Some (Choice2Of2 result) ->
            let obj = stateJson result
            obj["gameId"] <- JsonValue.Create(gameId)
            obj.ToJsonString()
