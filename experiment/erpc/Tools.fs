module TicTacToe.McpRpc.Tools

open System.Collections.Concurrent
open System.ComponentModel
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
type CompletedState =
    { Board: JsonObject; WhoseTurn: string; Status: string }

/// Cross-request state that MUST outlive a single tool invocation. The MCP SDK instantiates the tool
/// class per tools/call, so any state held as a TicTacToeTools instance field resets every request —
/// which made player_assigned re-fire on every move (seatedRoles reset) and dropped completed-game
/// snapshots (post-terminal reads failed). Registered as a singleton so all of it persists for the run.
///
/// Also the LOGIN REGISTRY. Over the multi-client HTTP host each agent connects on its OWN session and
/// logs in once: authenticate() mints a unique token and registers it here. A move is only accepted if it
/// bears a registered token (login is mandatory), and each client re-presents its own token per request —
/// its cookie jar — so identity is stable for the run and an agent cannot spoof or reconnect into another.
/// Identity is NOT a seat: X/O are assigned later by move order. (The stateful-HTTP session object can't be
/// used as the key — the SDK hands a fresh McpServer instance per request — so identity travels in the
/// registered bearer token, which the SDK special-cases nowhere and is therefore transport-independent.)
type ToolState() =
    let issued = ConcurrentDictionary<string, bool>()          // every token ever minted (the registry)
    member val CompletedGames = ConcurrentDictionary<string, CompletedState>()

    /// Mint + register a fresh unique login token.
    member _.Mint() : string =
        let t = System.Guid.NewGuid().ToString("N")
        issued.[t] <- true
        t

    /// Was this token issued by authenticate()? Unissued/blank tokens are not valid identities.
    member _.IsIssued(token: string) : bool =
        not (System.String.IsNullOrWhiteSpace token) && issued.ContainsKey token

[<McpServerToolType>]
type TicTacToeTools
    (
        supervisor: GameSupervisor,
        assignments: PlayerAssignmentStore,
        eventLog: EventLog,
        toolState: ToolState
    ) =

    // stdio transport is sequential (one request at a time), so these reads/writes cannot interleave.
    // Both live on the injected singleton (ToolState) — NOT instance fields — so they survive the
    // per-request tool lifetime. Retains finished games for post-game get_board/get_state.
    let completedGames = toolState.CompletedGames

    [<McpServerTool>]
    [<Description("Authenticate to obtain your identity token. Pass it as identityToken on every make_move so the server recognizes you. This is your login identity, not a seat — X or O is decided later by move order.")>]
    member _.authenticate() : AuthResponse =
        { token = toolState.Mint() }

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
    [<Description("Make a move. Authenticate first to get an identity token, then pass it as identityToken; the server derives your side (X or O) from it by move order. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.make_move
        (
            [<Description("The game ID returned by new_game")>] gameId: string,
            [<Description("Board position: TopLeft | TopCenter | TopRight | MiddleLeft | MiddleCenter | MiddleRight | BottomLeft | BottomCenter | BottomRight")>] position: string,
            [<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue("")>]
            [<Description("Your identity token from authenticate(). The server derives your side (X or O) from it by move order.")>] identityToken: string
        ) : obj =
        // Identity is the caller's registered login token, presented every request (its cookie jar). Only a
        // token that authenticate() issued counts — no login (blank/unknown token) -> no identity -> rejected.
        let token = if toolState.IsIssued identityToken then Some identityToken else None

        match resolveMove supervisor assignments token gameId position with
        | Moved(board, turn, status, side, newlySeated) ->
            let role = side.ToString()
            // player_assigned is emitted by the assignment authority (newlySeated), so it fires EXACTLY
            // once per seat — never per move — regardless of request/session lifecycle.
            if newlySeated then eventLog.LogEvent("player_assigned", gameId, role = role)
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
            eventLog.LogEvent("state_read", gameId, whoseTurn = whoseTurnStr result)
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
            // Symmetric read accounting: ERPC reads (polls) are otherwise invisible
            // to the event log, unlike the curl arms' GETs captured by the HTTP proxy.
            eventLog.LogEvent("state_read", gameId, whoseTurn = whoseTurnStr result)
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
