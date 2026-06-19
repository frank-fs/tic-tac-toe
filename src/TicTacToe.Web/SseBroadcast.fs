module TicTacToe.Web.SseBroadcast

open System
open System.IO
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Frank.Datastar

/// SSE event types for broadcasting to connected clients
type SseEvent =
    | PatchElements of render: (TextWriter -> Task)
    | PatchElementsAppend of selector: string * render: (TextWriter -> Task)
    | RemoveElement of selector: string
    | PatchSignals of json: string

/// Thread-safe collection of subscriber channels: (userId, gameFilter, channel).
/// gameFilter = None  -> dashboard subscriber (receives every game's events).
/// gameFilter = Some gameId -> per-game subscriber (receives only that game's events).
let private subscribers = ConcurrentDictionary<Guid, string * string option * Channel<SseEvent>>()

/// Create a new subscriber channel for an SSE connection.
/// gameFilter None = dashboard (all games); Some gameId = only that game.
/// Returns (Channel, IDisposable); disposing unsubscribes and completes the channel.
let subscribe (userId: string) (gameFilter: string option) : Channel<SseEvent> * IDisposable =
    let channel = Channel.CreateUnbounded<SseEvent>()
    let id = Guid.NewGuid()
    subscribers.TryAdd(id, (userId, gameFilter, channel)) |> ignore

    let disposable =
        { new IDisposable with
            member __.Dispose() =
                match subscribers.TryRemove(id) with
                | true, (_, _, ch) -> ch.Writer.Complete()
                | false, _ -> () }

    (channel, disposable)

/// A dashboard (None) subscriber receives every game; a Some-filtered subscriber
/// receives only its own game.
let private receivesGame (gameFilter: string option) (gameId: string) =
    match gameFilter with
    | None -> true
    | Some gid -> gid = gameId

/// Broadcast an event to ALL active SSE connections (used for global signals/removals).
let broadcast (event: SseEvent) =
    for KeyValue(_, (_, _, ch)) in subscribers do
        ch.Writer.TryWrite(event) |> ignore

/// Send an event to a specific user's SSE connections.
let sendToUser (userId: string) (event: SseEvent) =
    for KeyValue(_, (uid, _, ch)) in subscribers do
        if uid = userId then
            ch.Writer.TryWrite(event) |> ignore

/// Broadcast a per-role event for a specific game: reaches the dashboard subscribers
/// and that game's per-game subscribers, each rendered for their own userId.
let broadcastPerRoleForGame (gameId: string) (renderForRole: string -> SseEvent) =
    for KeyValue(_, (userId, gameFilter, ch)) in subscribers do
        if receivesGame gameFilter gameId then
            ch.Writer.TryWrite(renderForRole userId) |> ignore

/// Broadcast an event to dashboard (None-filter) subscribers only. Used for the
/// new-game append: a per-game subscriber must not receive another game's board.
let broadcastToDashboard (event: SseEvent) =
    for KeyValue(_, (_, gameFilter, ch)) in subscribers do
        if Option.isNone gameFilter then
            ch.Writer.TryWrite(event) |> ignore

/// Helper to write SSE events to response
let writeSseEvent (ctx: HttpContext) (event: SseEvent) =
    task {
        match event with
        | PatchElements render -> do! Datastar.streamPatchElements render ctx
        | PatchElementsAppend(selector, render) ->
            let opts = { PatchElementsOptions.Defaults with Selector = ValueSome (Selector selector); PatchMode = ElementPatchMode.Append }
            do! Datastar.streamPatchElementsWithOptions opts render ctx
        | RemoveElement selector -> do! Datastar.removeElement selector ctx
        | PatchSignals json -> do! Datastar.patchSignals json ctx
    }
