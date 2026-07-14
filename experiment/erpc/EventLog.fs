module TicTacToe.McpRpc.EventLog

open System
open System.IO
open System.Text.Json.Nodes

let private maxQueueDepth = 10_000

/// File-based, uniform per-game event log (one JSON object per line). MUST write to a
/// FILE, never stdout — stdout is reserved for MCP JSON-RPC. No-op when no path supplied.
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

    /// Emits BOTH vocabularies on one line: the app's own `role`/`move`/`reason` keys and the
    /// spec-001 §5 generic envelope (`actor` + opaque `payload`) the app-agnostic harness reads.
    /// One line, one truth — no second log to drift. This MUST match the HTTP arm's emitter:
    /// without `actor`/`payload` the IQualityScorer reads no moves and reports every seat
    /// `Clean=true, MoveCount=0` — a vacuous false-clean, not a real result.
    member _.LogEvent(eventType: string, gameId: string,
                      ?role: string, ?move: string, ?reason: string,
                      ?outcome: string, ?moveCount: int, ?whoseTurn: string) =
        let obj = JsonObject()
        obj["event_type"] <- JsonValue.Create(eventType)
        obj["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("o"))
        obj["game_id"] <- JsonValue.Create(gameId)
        role |> Option.iter (fun r -> obj["role"] <- JsonValue.Create(r))
        role |> Option.iter (fun r -> obj["actor"] <- JsonValue.Create(r))
        move |> Option.iter (fun m -> obj["move"] <- JsonValue.Create(m))
        reason |> Option.iter (fun r -> obj["reason"] <- JsonValue.Create(r))
        outcome |> Option.iter (fun o -> obj["outcome"] <- JsonValue.Create(o))
        moveCount |> Option.iter (fun n -> obj["move_count"] <- JsonValue.Create(n))
        whoseTurn |> Option.iter (fun t -> obj["whose_turn"] <- JsonValue.Create(t))
        let payload = JsonObject()
        move |> Option.iter (fun m -> payload["move"] <- JsonValue.Create(m))
        reason |> Option.iter (fun r -> payload["reason"] <- JsonValue.Create(r))
        obj["payload"] <- payload
        writeJson obj
