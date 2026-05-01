module TicTacToe.Orchestrator.RpcAgent

open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open TicTacToe.Engine
open TicTacToe.Model
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.AnthropicClient

let private maxTurns = 50

// ── Tool definitions (mirror H4 interface) ────────────────────────────────────

let private tools : ToolDef list = [
    { Name = "new_game"
      Description = "Create a new tic-tac-toe game. Returns a gameId. X always moves first."
      InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""") }

    { Name = "get_board"
      Description = "Get the current board state. Returns board (9 cells: 'X','O',''), whoseTurn, status ('in_progress'|'won'|'draw'), and validMoves."
      InputSchema = JsonNode.Parse("""{"type":"object","required":["gameId"],"properties":{"gameId":{"type":"string"}}}""") }

    { Name = "make_move"
      Description = "Make a move. player is 'X' or 'O'. position is one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight. Returns updated board on success, or a structured error."
      InputSchema = JsonNode.Parse("""{"type":"object","required":["gameId","player","position"],"properties":{"gameId":{"type":"string"},"player":{"type":"string","enum":["X","O"]},"position":{"type":"string"}}}""") }

    { Name = "get_state"
      Description = "Get full game state including gameId, board, whoseTurn, status, and validMoves."
      InputSchema = JsonNode.Parse("""{"type":"object","required":["gameId"],"properties":{"gameId":{"type":"string"}}}""") }
]

// ── Engine helpers ─────────────────────────────────────────────────────────────

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) =
    allPositions |> Array.map (fun pos ->
        match gs.TryGetValue(pos) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private statusStr result =
    match result with
    | XTurn _ -> "in_progress" | OTurn _ -> "in_progress"
    | Won _ -> "won" | Draw _ -> "draw" | Error _ -> "error"

let private whoseTurnStr result =
    match result with
    | XTurn _ -> "X" | OTurn _ -> "O"
    | Won _ -> "game_over" | Draw _ -> "draw" | Error _ -> "error"

let private validMovesArr result =
    match result with
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

let private getGs result =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

// ── Tool dispatch ──────────────────────────────────────────────────────────────

let private dispatchTool (supervisor: GameSupervisor) (call: ToolCall) : string * OutcomeTag =
    match call.Name with
    | "new_game" ->
        let (gameId, _game) = supervisor.CreateGame()
        let result = JsonObject()
        result["gameId"] <- JsonValue.Create(gameId)
        (result.ToJsonString(), ValidAction)

    | "get_board" ->
        let gameId = call.Input["gameId"].GetValue<string>()
        match supervisor.GetGame(gameId) with
        | None ->
            let err = JsonObject()
            err["error"] <- JsonValue.Create("game_not_found")
            (err.ToJsonString(), InvalidAction)
        | Some game ->
            let result = game.GetState()
            let gs = getGs result
            let resp = JsonObject()
            resp["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
            resp["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
            resp["status"] <- JsonValue.Create(statusStr result)
            resp["validMoves"] <- JsonNode.Parse(JsonSerializer.Serialize(validMovesArr result))
            (resp.ToJsonString(), Discovery)

    | "make_move" ->
        let gameId = call.Input["gameId"].GetValue<string>()
        let player = call.Input["player"].GetValue<string>()
        let position = call.Input["position"].GetValue<string>()
        match supervisor.GetGame(gameId) with
        | None ->
            let err = JsonObject()
            err["error"] <- JsonValue.Create("game_not_found")
            (err.ToJsonString(), InvalidAction)
        | Some game ->
            match Move.TryParse(player, position) with
            | None ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create("invalid_input")
                (err.ToJsonString(), InvalidAction)
            | Some move ->
                game.MakeMove(move)
                let result = game.GetState()
                match result with
                | Error(_, msg) ->
                    let err = JsonObject()
                    err["error"] <- JsonValue.Create(msg)
                    (err.ToJsonString(), InvalidAction)
                | _ ->
                    let gs = getGs result
                    let resp = JsonObject()
                    resp["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
                    resp["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
                    resp["status"] <- JsonValue.Create(statusStr result)
                    (resp.ToJsonString(), ValidAction)

    | "get_state" ->
        let gameId = call.Input["gameId"].GetValue<string>()
        match supervisor.GetGame(gameId) with
        | None ->
            let err = JsonObject()
            err["error"] <- JsonValue.Create("game_not_found")
            (err.ToJsonString(), InvalidAction)
        | Some game ->
            let result = game.GetState()
            let gs = getGs result
            let resp = JsonObject()
            resp["gameId"] <- JsonValue.Create(gameId)
            resp["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
            resp["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
            resp["status"] <- JsonValue.Create(statusStr result)
            resp["validMoves"] <- JsonNode.Parse(JsonSerializer.Serialize(validMovesArr result))
            (resp.ToJsonString(), Discovery)

    | name ->
        let err = JsonObject()
        err["error"] <- JsonValue.Create(sprintf "unknown_tool: %s" name)
        (err.ToJsonString(), InvalidAction)

// ── Game loop ─────────────────────────────────────────────────────────────────

/// Run one game using E_RPC setup (no HTTP).
/// Returns (transcript entries as Tool records, total tokens consumed).
let runGame
    (model: string)
    (temperature: float)
    (systemPrompt: string)
    : Task<TranscriptEntry list * int> =
    task {
        let supervisor = createGameSupervisor()
        let messages = JsonArray()
        appendUserText messages "Start a new tic-tac-toe game and play it to completion using the provided tools." |> ignore

        let mutable transcript: ToolEntry list = []
        let mutable totalTokens = 0
        let mutable turn = 0
        let mutable keepGoing = true

        while keepGoing && turn < maxTurns do
            let! result = runTurn model temperature (Some systemPrompt) tools messages
            match result with
            | Done(_, inp, out) ->
                totalTokens <- totalTokens + inp + out
                keepGoing <- false
            | ToolCalls(calls, inp, out) ->
                totalTokens <- totalTokens + inp + out
                appendAssistantToolUse messages calls |> ignore

                let toolResults = System.Collections.Generic.List<string * string>()
                for call in calls do
                    turn <- turn + 1
                    let (output, outcome) = dispatchTool supervisor call
                    let entry = {
                        Turn = turn
                        ToolUseId = call.Id
                        ToolName = call.Name
                        Input = call.Input.ToJsonString()
                        Output = output
                        Outcome = outcome
                    }
                    transcript <- transcript @ [entry]
                    toolResults.Add(call.Id, output)

                appendToolResults messages (toolResults |> Seq.toList) |> ignore

        return (transcript |> List.map Tool, totalTokens)
    }
