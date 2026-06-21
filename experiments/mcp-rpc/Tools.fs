module TicTacToe.McpRpc.Tools

open System.Collections.Concurrent
open System.ComponentModel
open System.Security.Claims
open System.Text.Json
open System.Text.Json.Nodes
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Model
open TicTacToe.McpRpc.Identity
open TicTacToe.McpRpc.EventLog

type AuthResponse = { token: string }
type NewGameResponse = { gameId: string }

/// Final snapshot of a finished game, retained for reads after the supervisor
/// drops it (GameSupervisor removes a game on completion — Engine.fs OnCompleted).
type private CompletedState =
    { Board: JsonObject; WhoseTurn: string; Status: string }

[<McpServerToolType>]
type TicTacToeTools
    (
        supervisor: GameSupervisor,
        assignments: PlayerAssignmentStore,
        eventLog: EventLog
    ) =

    // stdio transport is sequential (one request at a time), so these reads/writes
    // cannot interleave. Retains finished games for post-game get_board/get_state.
    let completedGames = ConcurrentDictionary<string, CompletedState>()

    // First-seat tracking per (gameId, side) so player_assigned fires once per role.
    let seatedRoles = ConcurrentDictionary<string, bool>()

    [<McpServerTool>]
    [<Description("Authenticate as a player. Returns an identity token; pass it on each subsequent call's `_meta.identityToken` to bind your moves to a seat.")>]
    member _.authenticate() : AuthResponse =
        { token = System.Guid.NewGuid().ToString("N") }

    [<McpServerTool>]
    [<Description("List all active in-progress games. Returns an array of {gameId, whoseTurn, status}.")>]
    member _.list_games() : string =
        let games =
            supervisor.ListActiveGames()
            |> List.choose (fun id ->
                match supervisor.GetGame id with
                | None -> None
                | Some game ->
                    let result = game.GetState()
                    let obj = JsonObject()
                    obj["gameId"] <- JsonValue.Create(id)
                    obj["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
                    obj["status"] <- JsonValue.Create(statusStr result)
                    Some(obj :> JsonNode))
        JsonSerializer.Serialize(games)

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns a gameId to use in subsequent calls. X always moves first. Only one game may be active at a time; if one already exists this returns {error: \"MaxGamesReached\"}.")>]
    member _.new_game() : obj =
        match supervisor.TryCreateGame(Some 1) with
        | None -> box {| error = "MaxGamesReached" |}
        | Some(gameId, _) -> box { gameId = gameId }

    [<McpServerTool>]
    [<Description("Make a move. Authenticate first to get an identity token, then pass it as identityToken (or via _meta.identityToken); the server derives your side (X or O) from it. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.make_move
        (
            user: ClaimsPrincipal,
            [<Description("The game ID returned by new_game")>] gameId: string,
            [<Description("Board position: TopLeft | TopCenter | TopRight | MiddleLeft | MiddleCenter | MiddleRight | BottomLeft | BottomCenter | BottomRight")>] position: string,
            [<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue("")>]
            [<Description("Your identity token from authenticate(). Pass it here when your client cannot set _meta.identityToken (e.g. a generic MCP client). Falls back to _meta if omitted.")>] identityToken: string
        ) : obj =
        // Explicit param wins (portable to any MCP client); else fall back to the
        // _meta-bridged ClaimsPrincipal (the bespoke orchestrator's per-request path).
        let token =
            if not (System.String.IsNullOrWhiteSpace identityToken) then Some identityToken
            else
                match user with
                | null -> None
                | u -> u.Identity |> Option.ofObj |> Option.bind (fun i -> Option.ofObj i.Name)

        match resolveMove supervisor assignments token gameId position with
        | Moved(board, turn, status, side) ->
            let role = side.ToString()
            if seatedRoles.TryAdd($"{gameId}:{role}", true) then
                eventLog.LogEvent("player_assigned", gameId, role = role)
            eventLog.LogEvent("move_accepted", gameId, role = role, move = position)
            if status = "won" || status = "draw" then
                completedGames[gameId] <- { Board = board; WhoseTurn = turn; Status = status }
                let moveCount =
                    board |> Seq.filter (fun kv -> kv.Value <> null && kv.Value.GetValue<string>() <> "") |> Seq.length
                // whoseTurn on a won game is "X won"/"O won"; normalize to "x_wins"/"o_wins".
                let outcome =
                    if status = "draw" then "draw"
                    else $"""{turn.Split(' ').[0].ToLower()}_wins"""
                eventLog.LogEvent("game_over", gameId, outcome = outcome, moveCount = moveCount)
            box {| board = board; whoseTurn = turn; status = status |}
        | MoveOutcome.Rejected code ->
            let role =
                token
                |> Option.bind (fun t -> assignments.SeatOf(gameId, t))
                |> Option.defaultValue "unassigned"
            eventLog.LogEvent("move_rejected", gameId, role = role, reason = code)
            box {| error = code; position = position |}

    [<McpServerTool>]
    [<Description("Get the current board state for a game: 9 squares keyed by name (TopLeft..BottomRight) with value \"X\"/\"O\"/\"\", whose turn it is, status, and valid moves.")>]
    member _.get_board([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame gameId with
        | Some game ->
            let result = game.GetState()
            box
                {| board = renderBoard (stateOf result)
                   whoseTurn = whoseTurnStr result
                   status = statusStr result
                   validMoves = validMoves result |}
        | None ->
            match completedGames.TryGetValue gameId with
            | true, c ->
                box
                    {| board = c.Board
                       whoseTurn = c.WhoseTurn
                       status = c.Status
                       validMoves = ([||]: string[]) |}
            | _ -> box {| error = "game_not_found"; gameId = gameId |}

    [<McpServerTool>]
    [<Description("Get full game state including gameId, board (9 squares keyed by name, value \"X\"/\"O\"/\"\"), whose turn, status, and valid moves.")>]
    member _.get_state([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame gameId with
        | Some game ->
            let result = game.GetState()
            box
                {| gameId = gameId
                   board = renderBoard (stateOf result)
                   whoseTurn = whoseTurnStr result
                   status = statusStr result
                   validMoves = validMoves result |}
        | None ->
            match completedGames.TryGetValue gameId with
            | true, c ->
                box
                    {| gameId = gameId
                       board = c.Board
                       whoseTurn = c.WhoseTurn
                       status = c.Status
                       validMoves = ([||]: string[]) |}
            | _ -> box {| error = "game_not_found"; gameId = gameId |}
