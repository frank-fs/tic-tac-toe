module TicTacToe.Orchestrator.ServerLogTail

open System
open System.IO
open System.Text.Json.Nodes
open TicTacToe.Orchestrator.Types

let parseLogLine (json: string) : ServerLogEvent option =
    try
        let obj = JsonNode.Parse(json) :?> JsonObject
        let ts () = DateTimeOffset.Parse(obj["timestamp"].GetValue<string>())

        let mutable evTypeNode: JsonNode = null
        if obj.TryGetPropertyValue("event_type", &evTypeNode) && evTypeNode <> null then
            match evTypeNode.GetValue<string>() with
            | "game_created" ->
                Some(GameCreated(obj["game_id"].GetValue<string>(), ts()))
            | "player_assigned" ->
                Some(PlayerAssigned(
                    obj["game_id"].GetValue<string>(),
                    obj["session_id"].GetValue<string>(),
                    obj["role"].GetValue<string>(),
                    ts()))
            | "move_accepted" ->
                Some(MoveAccepted(
                    obj["game_id"].GetValue<string>(),
                    obj["session_id"].GetValue<string>(),
                    obj["move"].GetValue<string>(),
                    ts()))
            | "game_over" ->
                Some(GameOver(
                    obj["game_id"].GetValue<string>(),
                    obj["outcome"].GetValue<string>(),
                    obj["move_count"].GetValue<int>(),
                    ts()))
            | _ -> None
        else
            let mutable statusNode: JsonNode = null
            let mutable rejNode: JsonNode = null
            let mutable gidNode: JsonNode = null
            let mutable sidNode: JsonNode = null
            if obj.TryGetPropertyValue("status_code", &statusNode) && statusNode <> null
               && statusNode.GetValue<int>() = 403
               && obj.TryGetPropertyValue("rejection_reason", &rejNode) && rejNode <> null
               && obj.TryGetPropertyValue("game_id", &gidNode) && gidNode <> null
               && obj.TryGetPropertyValue("session_id", &sidNode) && sidNode <> null then
                try
                    Some(MoveRejected(
                        gidNode.GetValue<string>(),
                        sidNode.GetValue<string>(),
                        rejNode.GetValue<string>(),
                        ts()))
                with _ -> None
            else None
    with _ -> None

type LogTail(path: string) =
    member _.GetEvents() =
        if not (File.Exists(path)) then []
        else
            File.ReadAllLines(path)
            |> Array.toList
            |> List.choose parseLogLine

let startTail (path: string) : LogTail = LogTail(path)
