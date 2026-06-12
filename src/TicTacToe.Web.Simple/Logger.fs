module TicTacToe.Web.Simple.Logger

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open TicTacToe.Model

type RequestLogger(?logPath: string) =
    let writer =
        logPath |> Option.map (fun path ->
            MailboxProcessor<string>.Start(fun inbox ->
                let rec loop () =
                    async {
                        let! line = inbox.Receive()
                        File.AppendAllText(path, line + "\n")
                        return! loop ()
                    }
                loop ()))

    let writeJson (obj: JsonObject) =
        match writer with
        | None -> ()
        | Some mp -> mp.Post(obj.ToJsonString())

    let boardArray (result: MoveResult) =
        let allPositions =
            [| TopLeft; TopCenter; TopRight
               MiddleLeft; MiddleCenter; MiddleRight
               BottomLeft; BottomCenter; BottomRight |]
        let gs =
            match result with
            | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs
        allPositions |> Array.map (fun pos ->
            match gs.TryGetValue(pos) with
            | true, Taken X -> "X"
            | true, Taken O -> "O"
            | _ -> "")

    member _.LogRequest(requestId: string, sessionId: string, gameId: string option, playerRole: string,
                        method: string, path: string, statusCode: int,
                        rejectionReason: string option,
                        boardBefore: MoveResult option, boardAfter: MoveResult option) =
        let obj = JsonObject()
        obj["request_id"] <- JsonValue.Create(requestId)
        obj["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("o"))
        obj["session_id"] <- JsonValue.Create(sessionId)
        obj["game_id"] <- gameId |> Option.map JsonValue.Create<string> |> Option.defaultValue (JsonValue.Create(null: string)) :> JsonNode
        obj["player_role"] <- JsonValue.Create(playerRole)
        obj["method"] <- JsonValue.Create(method)
        obj["path"] <- JsonValue.Create(path)
        obj["status_code"] <- JsonValue.Create(statusCode)
        obj["rejection_reason"] <- rejectionReason |> Option.map JsonValue.Create<string> |> Option.defaultValue (JsonValue.Create(null: string)) :> JsonNode
        obj["board_state_before"] <- boardBefore |> Option.map (fun r -> JsonNode.Parse(JsonSerializer.Serialize(boardArray r))) |> Option.defaultValue (JsonValue.Create(null: string) :> JsonNode)
        obj["board_state_after"] <- boardAfter |> Option.map (fun r -> JsonNode.Parse(JsonSerializer.Serialize(boardArray r))) |> Option.defaultValue (JsonValue.Create(null: string) :> JsonNode)
        writeJson obj

    member _.LogEvent(eventType: string, gameId: string, ?role: string, ?outcome: string, ?moveCount: int, ?move: string) =
        let obj = JsonObject()
        obj["event_type"] <- JsonValue.Create(eventType)
        obj["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("o"))
        obj["game_id"] <- JsonValue.Create(gameId)
        role |> Option.iter (fun r -> obj["role"] <- JsonValue.Create(r))
        outcome |> Option.iter (fun o -> obj["outcome"] <- JsonValue.Create(o))
        moveCount |> Option.iter (fun n -> obj["move_count"] <- JsonValue.Create(n))
        move |> Option.iter (fun m -> obj["move"] <- JsonValue.Create(m))
        writeJson obj
