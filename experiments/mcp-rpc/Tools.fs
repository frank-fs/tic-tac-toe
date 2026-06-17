module TicTacToe.McpRpc.Tools

open System.ComponentModel
open System.Security.Claims
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Model
open TicTacToe.McpRpc.Identity

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

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
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error(_, msg) -> sprintf "error: %s" msg

let private validMoves (result: MoveResult) : string[] =
    match result with
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

let private stateOf (result: MoveResult) : GameState =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

type AuthResponse = { token: string }
type NewGameResponse = { gameId: string }

[<McpServerToolType>]
type TicTacToeTools
    (
        supervisor: GameSupervisor,
        session: SessionIdentity,
        assignments: PlayerAssignmentStore
    ) =

    [<McpServerTool>]
    [<Description("Authenticate as a player. Returns a token bound to this connection. Call this once before make_move; subsequent calls carry your identity automatically.")>]
    member _.authenticate() : AuthResponse =
        { token = session.Authenticate() }

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns a gameId to use in subsequent calls. X always moves first.")>]
    member _.new_game() : NewGameResponse =
        let gameId, _ = supervisor.CreateGame()
        { gameId = gameId }

    [<McpServerTool>]
    [<Description("Make a move. You must authenticate first; the server derives your side (X or O) from your identity. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.make_move
        (
            user: ClaimsPrincipal,
            [<Description("The game ID returned by new_game")>] gameId: string,
            [<Description("Board position: TopLeft | TopCenter | TopRight | MiddleLeft | MiddleCenter | MiddleRight | BottomLeft | BottomCenter | BottomRight")>] position: string
        ) : obj =
        let token =
            match user with
            | null -> None
            | u -> u.Identity |> Option.ofObj |> Option.bind (fun i -> Option.ofObj i.Name)

        match resolveMove supervisor assignments token gameId position with
        | Moved(board, turn, status) -> box {| board = board; whoseTurn = turn; status = status |}
        | MoveOutcome.Rejected code -> box {| error = code; position = position |}

    [<McpServerTool>]
    [<Description("Get the current board state for a game: 9 cells (index 0=TopLeft to 8=BottomRight), whose turn it is, status, and valid moves.")>]
    member _.get_board([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame(gameId) with
        | None -> box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            let result = game.GetState()
            box
                {| board = renderBoard (stateOf result)
                   whoseTurn = whoseTurn result
                   status = statusStr result
                   validMoves = validMoves result |}

    [<McpServerTool>]
    [<Description("Get full game state including board, turn, status, and valid moves for the game.")>]
    member _.get_state([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame(gameId) with
        | None -> box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            let result = game.GetState()
            box
                {| gameId = gameId
                   board = renderBoard (stateOf result)
                   whoseTurn = whoseTurn result
                   status = statusStr result
                   validMoves = validMoves result |}
