module TicTacToe.Web.EventLog

open System
open System.IO
open System.Text.Json.Nodes

let private maxQueueDepth = 10_000

/// File-based, uniform per-game event log (one JSON object per line). No-op when
/// no path is supplied, so production runs without the orchestrator pay nothing.
type EventLog(?logPath: string) =
    let writer =
        logPath |> Option.map (fun path ->
            let sw = new StreamWriter(path, append = true, AutoFlush = true)
            MailboxProcessor<string>.Start(fun inbox ->
                let rec loop () =
                    async {
                        let! line = inbox.Receive()
                        do! sw.WriteLineAsync(line) |> Async.AwaitTask
                        return! loop ()
                    }
                loop ()))

    let writeJson (obj: JsonObject) =
        match writer with
        | None -> ()
        | Some mp ->
            if mp.CurrentQueueLength < maxQueueDepth then
                mp.Post(obj.ToJsonString())
            // drop silently when saturated — log loss acceptable over crash

    member _.LogEvent(eventType: string, gameId: string,
                      ?role: string, ?move: string, ?reason: string,
                      ?outcome: string, ?moveCount: int) =
        let obj = JsonObject()
        obj["event_type"] <- JsonValue.Create(eventType)
        obj["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("o"))
        obj["game_id"] <- JsonValue.Create(gameId)
        role |> Option.iter (fun r -> obj["role"] <- JsonValue.Create(r))
        move |> Option.iter (fun m -> obj["move"] <- JsonValue.Create(m))
        reason |> Option.iter (fun r -> obj["reason"] <- JsonValue.Create(r))
        outcome |> Option.iter (fun o -> obj["outcome"] <- JsonValue.Create(o))
        moveCount |> Option.iter (fun n -> obj["move_count"] <- JsonValue.Create(n))
        writeJson obj
