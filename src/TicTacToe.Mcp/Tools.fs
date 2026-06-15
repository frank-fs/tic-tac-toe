module TicTacToe.Mcp.Tools

open System.Collections.Concurrent
open System.Collections.Generic
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
    | NotAPlayer
    | NotYourTurn
    | GameFull
    | RoleTaken

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) =
    let obj = JsonObject()
    for pos in allPositions do
        let label =
            match gs.TryGetValue(pos) with
            | true, Taken X -> "X"
            | true, Taken O -> "O"
            | _ -> ""
        obj[pos.ToString()] <- JsonValue.Create(label)
    obj

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

let private stateJson (result: MoveResult) =
    let gs = getGs result
    let obj = JsonObject()
    obj["board"] <- renderBoard gs
    obj["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
    obj["status"] <- JsonValue.Create(statusStr result)
    obj

let private errorJson (reason: RejectionReason) =
    let obj = JsonObject()
    obj["error"] <- JsonValue.Create(reason.ToString())
    obj.ToJsonString()

let private isTerminal = function
    | Won _ | Draw _ -> true
    | _ -> false

let private applyMove (game: Game) (currentResult: MoveResult) (position: string) : string =
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
            (stateJson (game.GetState())).ToJsonString()

// Per-agent role binding lives in a SINGLETON (the MCP framework resolves a fresh tool
// instance per request, so tool-instance fields don't persist; game state survives only
// because GameSupervisor is a singleton). Agents share one stdio connection, so identity
// rides in a playerToken (mirrors Simple's cookie->role). join claims X then O; a third
// claimant -> GameFull.
type PlayerRegistry() =
    let tokenRole = ConcurrentDictionary<string, string * Player>()   // token -> (gameId, role)
    let gameClaims = ConcurrentDictionary<string, HashSet<Player>>()  // gameId -> claimed roles

    member _.Join(gameId: string, preferred: Player option) : Choice<string * Player, RejectionReason> =
        let claimed = gameClaims.GetOrAdd(gameId, fun _ -> HashSet<Player>())
        let assign =
            match preferred with
            | Some r -> if claimed.Contains r then None else Some r
            | None ->
                if not (claimed.Contains X) then Some X
                elif not (claimed.Contains O) then Some O
                else None
        match assign with
        | None when preferred.IsSome -> Choice2Of2 RoleTaken
        | None -> Choice2Of2 GameFull
        | Some role ->
            claimed.Add role |> ignore
            let token = System.Guid.NewGuid().ToString("N")
            tokenRole[token] <- (gameId, role)
            Choice1Of2 (token, role)

    member _.Resolve(token: string) : (string * Player) option =
        match tokenRole.TryGetValue(token) with
        | true, v -> Some v
        | _ -> None

[<McpServerToolType>]
type GameTools(supervisor: GameSupervisor, players: PlayerRegistry) =
    // stdio transport is sequential (one request at a time), so TryAdd and RemoveGame cannot interleave.
    let completedGames = ConcurrentDictionary<string, MoveResult>()

    let parseRole = function
        | "X" -> Some X | "O" -> Some O | _ -> None

    let resolveGame gameId =
        match supervisor.GetGame(gameId) with
        | Some game -> Some (Choice1Of2 game)
        | None ->
            match completedGames.TryGetValue(gameId) with
            | true, result -> Some (Choice2Of2 result)
            | _ -> None

    [<McpServerTool>]
    [<Description("List all active in-progress games. Returns array of {gameId, whoseTurn, status}.")>]
    member _.``list_games``() : string =
        let gameIds = supervisor.ListActiveGames()
        let games =
            gameIds
            |> List.choose (fun id ->
                match supervisor.GetGame(id) with
                | Some game ->
                    let state = game.GetState()
                    let obj = JsonObject()
                    obj["gameId"] <- JsonValue.Create(id)
                    obj["whoseTurn"] <- JsonValue.Create(whoseTurnStr state)
                    obj["status"] <- JsonValue.Create(statusStr state)
                    Some (obj :> JsonNode)
                | None -> None)
        JsonSerializer.Serialize(games)

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns gameId, board (9 squares keyed by name), whoseTurn, and status.")>]
    member _.``new_game``() : string =
        if supervisor.ListActiveGames() |> List.length >= 1 then
            let obj = JsonObject()
            obj["error"] <- JsonValue.Create("MaxGamesReached")
            obj.ToJsonString()
        else
            let gameId, _ = supervisor.CreateGame()
            match supervisor.GetGame(gameId) with
            | None -> errorJson GameNotFound
            | Some game ->
                let obj = stateJson (game.GetState())
                obj["gameId"] <- JsonValue.Create(gameId)
                obj.ToJsonString()

    [<McpServerTool>]
    [<Description("Get board state for a game. Returns board (9 squares keyed by name, value \"X\"/\"O\"/\"\"), whoseTurn, and status.")>]
    member _.``get_board``(
        [<Description("Game ID")>] gameId: string) : string =
        match resolveGame gameId with
        | None -> errorJson GameNotFound
        | Some (Choice1Of2 game) -> (stateJson (game.GetState())).ToJsonString()
        | Some (Choice2Of2 result) -> (stateJson result).ToJsonString()

    [<McpServerTool>]
    [<Description("Join a game to claim your player role. preferredRole is \"X\", \"O\", or \"\" for the next free role. Returns {gameId, role, playerToken}; pass playerToken to make_move. X moves first. A game has exactly two seats — a third join returns error GameFull.")>]
    member _.``join_game``(
        [<Description("Game ID")>] gameId: string,
        [<Description("Preferred role: \"X\", \"O\", or \"\" for next available")>] preferredRole: string) : string =
        match resolveGame gameId with
        | None -> errorJson GameNotFound
        | Some (Choice2Of2 _) -> errorJson GameOver
        | Some (Choice1Of2 _) ->
            match players.Join(gameId, parseRole preferredRole) with
            | Choice2Of2 reason -> errorJson reason
            | Choice1Of2 (token, role) ->
                let obj = JsonObject()
                obj["gameId"] <- JsonValue.Create(gameId)
                obj["role"] <- JsonValue.Create(match role with X -> "X" | O -> "O")
                obj["playerToken"] <- JsonValue.Create(token)
                obj.ToJsonString()

    [<McpServerTool>]
    [<Description("Make a move. Requires the playerToken from join_game; the move is made as your claimed role and only succeeds on your turn. position is one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.``make_move``(
        [<Description("Game ID")>] gameId: string,
        [<Description("Your playerToken from join_game")>] playerToken: string,
        [<Description("Square to claim, e.g. TopLeft")>] position: string) : string =
        match resolveGame gameId with
        | None -> errorJson GameNotFound
        | Some (Choice2Of2 _) -> errorJson GameOver
        | Some (Choice1Of2 game) ->
            match players.Resolve(playerToken) with
            | Some (tGid, role) when tGid = gameId ->
                let currentResult = game.GetState()
                match currentResult with
                | Won _ | Draw _ ->
                    completedGames.TryAdd(gameId, currentResult) |> ignore
                    errorJson GameOver
                | XTurn _ when role <> X -> errorJson NotYourTurn
                | OTurn _ when role <> O -> errorJson NotYourTurn
                | currentResult ->
                    let json = applyMove game currentResult position
                    let nextResult = game.GetState()
                    if isTerminal nextResult then
                        completedGames.TryAdd(gameId, nextResult) |> ignore
                    json
            | _ -> errorJson NotAPlayer

    [<McpServerTool>]
    [<Description("Get full game state including gameId, board (9 squares keyed by name, value \"X\"/\"O\"/\"\"), whoseTurn, and status.")>]
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
