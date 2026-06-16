module TicTacToe.Engine

open System
open System.Threading
open TicTacToe.Model

type Game =
    inherit IDisposable
    inherit IObservable<MoveResult>
    abstract MakeMove: Move -> unit
    abstract GetState: unit -> MoveResult

/// Internal message type for the game actor
type GameMessage =
    | MakeMove of Move
    | Subscribe of IObserver<MoveResult> * AsyncReplyChannel<IDisposable>
    | Unsubscribe of int
    | GetState of AsyncReplyChannel<MoveResult>
    | Stop

/// Internal actor state for managing game and subscribers
type private GameActorState = {
    GameState: MoveResult
    Subscribers: Map<int, IObserver<MoveResult>>
    NextId: int
    Completed: bool
}

/// Game actor implementation using pure MailboxProcessor with IObservable interface
type GameImpl() =
    let initialState = startGame ()
    let mutable disposed = false

    let agent =
        MailboxProcessor<GameMessage>.Start(fun inbox ->
            /// Broadcast state to all subscribers with error protection
            let notifyAll (subs: Map<int, IObserver<MoveResult>>) result =
                subs |> Map.iter (fun _ observer ->
                    try observer.OnNext(result) with _ -> ())

            /// Call OnCompleted for all subscribers with error protection
            let notifyComplete (subs: Map<int, IObserver<MoveResult>>) =
                subs |> Map.iter (fun _ observer ->
                    try observer.OnCompleted() with _ -> ())

            let rec messageLoop (state: GameActorState) =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Stop ->
                        if not state.Completed then
                            notifyComplete state.Subscribers

                    | GetState reply ->
                        reply.Reply(state.GameState)
                        return! messageLoop state

                    | Subscribe(observer, reply) ->
                        let id = state.NextId
                        let handle =
                            { new IDisposable with
                                member _.Dispose() = inbox.Post(Unsubscribe id) }
                        let newSubs = state.Subscribers |> Map.add id observer
                        // Emit current state immediately (BehaviorSubject semantics)
                        try observer.OnNext(state.GameState) with _ -> ()
                        reply.Reply(handle)
                        return! messageLoop { state with Subscribers = newSubs; NextId = id + 1 }

                    | Unsubscribe id ->
                        let newSubs = state.Subscribers |> Map.remove id
                        return! messageLoop { state with Subscribers = newSubs }

                    | MakeMove move ->
                        let nextResult = makeMove (state.GameState, move)
                        notifyAll state.Subscribers nextResult

                        match nextResult with
                        | Won _ | Draw _ ->
                            notifyComplete state.Subscribers
                            return! messageLoop { state with GameState = nextResult; Completed = true }
                        | Error _ ->
                            notifyAll state.Subscribers state.GameState
                            return! messageLoop state
                        | XTurn _ | OTurn _ ->
                            return! messageLoop { state with GameState = nextResult }
                }

            messageLoop {
                GameState = initialState
                Subscribers = Map.empty
                NextId = 0
                Completed = false
            })

    interface Game with
        member _.MakeMove(move: Move) =
            if disposed then
                raise (ObjectDisposedException("Game"))
            else
                agent.Post(MakeMove move)

        member _.GetState() =
            // Guard against messaging a Stopped actor (would hang forever): fail fast instead.
            if disposed then
                raise (ObjectDisposedException("Game"))
            else
                agent.PostAndReply(GetState)

        member _.Dispose() =
            if not disposed then
                disposed <- true
                agent.Post(Stop)

    interface IObservable<MoveResult> with
        member _.Subscribe(observer) =
            // Same Stopped-actor guard as GetState: a Subscribe after Dispose would hang on PostAndReply.
            if disposed then
                raise (ObjectDisposedException("Game"))
            else
                agent.PostAndReply(fun reply -> Subscribe(observer, reply))

let createGame () : Game = new GameImpl() :> Game

type GameSupervisor =
    inherit IDisposable
    abstract CreateGame: unit -> string * Game
    abstract GetGame: gameId: string -> Game option
    abstract GetActiveGameCount: unit -> int
    abstract ListActiveGames: unit -> string list
    abstract SnapshotActiveGames: unit -> (string * MoveResult) list
    abstract TryGetState: gameId: string -> MoveResult option
    abstract TryCreateGame: maxGames: int option -> (string * Game) option

type GameRef =
    { Game: Game
      Subscription: IDisposable
      Timestamp: DateTimeOffset
      // Latest state, kept current via the supervisor's own subscription, which posts
      // UpdateState back onto this mailbox. Lets Snapshot read state without messaging each
      // game actor (which would hang on a mid-disposal Stopped actor) and keeps every read
      // and write of game state on the single supervisor thread — no cross-thread ref.
      LatestState: MoveResult }

type GameSupervisorMessage =
    | CountActive of AsyncReplyChannel<int>
    | ListGames of AsyncReplyChannel<string list>
    | Snapshot of AsyncReplyChannel<(string * MoveResult) list>
    | GetStateOf of string * AsyncReplyChannel<MoveResult option>
    | UpdateState of string * MoveResult
    | CreateGame of AsyncReplyChannel<string * Game>
    | TryCreate of int option * AsyncReplyChannel<(string * Game) option>
    | GetGame of string * AsyncReplyChannel<Game option>
    | RemoveGame of string
    | Timeout
    | Dispose

type GameSupervisorImpl() as this =
    let mutable disposed = false

    let cleanupTimer =
        new Timer((fun _ -> this.CleanupExpiredGames()), null, TimeSpan.FromMinutes(5.0), TimeSpan.FromMinutes(5.0))

    let agent =
        MailboxProcessor<GameSupervisorMessage>.Start(fun inbox ->
            // Create a game, subscribe for state caching, and add it to the map.
            // The first emit is synchronous (BehaviorSubject semantics) and runs during
            // Subscribe, so we capture it to seed LatestState — never null. Later emits arrive
            // on the game-actor thread and post UpdateState, keeping all state on this mailbox.
            let createAndAdd state =
                let gameId = Guid.NewGuid().ToString()
                let game = createGame ()
                let timestamp = DateTimeOffset.UtcNow
                let mutable initial = ValueNone

                let subscription =
                    game.Subscribe(
                        { new IObserver<MoveResult> with
                            member _.OnNext(result) =
                                match initial with
                                | ValueNone -> initial <- ValueSome result
                                | ValueSome _ -> this.UpdateState(gameId, result)
                            member _.OnCompleted() = this.RemoveGame(gameId)
                            member _.OnError(_) = this.RemoveGame(gameId) }
                    )

                let initialState =
                    match initial with
                    | ValueSome s -> s
                    | ValueNone -> failwith "Game.Subscribe must emit current state synchronously"

                let gameRef =
                    { Game = game
                      Timestamp = timestamp
                      Subscription = subscription
                      LatestState = initialState }

                gameId, game, Map.add gameId gameRef state

            let rec messageLoop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | CountActive reply ->
                        reply.Reply(Map.count state)
                        return! messageLoop state

                    | ListGames reply ->
                        reply.Reply(state |> Map.toList |> List.map fst)
                        return! messageLoop state

                    | Snapshot reply ->
                        // Read cached latest states — no per-game actor round-trip, so a game
                        // mid-disposal cannot hang the supervisor; one round-trip for the caller.
                        reply.Reply(state |> Map.toList |> List.map (fun (id, gr) -> id, gr.LatestState))
                        return! messageLoop state

                    | GetStateOf(gameId, reply) ->
                        // Cached read: None if the game is gone (or being disposed), so callers
                        // 404 instead of hanging on a Stopped game actor.
                        reply.Reply(state |> Map.tryFind gameId |> Option.map (fun gr -> gr.LatestState))
                        return! messageLoop state

                    | UpdateState(gameId, result) ->
                        // Cache update from a game's subscription, applied on this thread only.
                        match Map.tryFind gameId state with
                        | Some gr -> return! messageLoop (Map.add gameId { gr with LatestState = result } state)
                        | None -> return! messageLoop state

                    | CreateGame reply ->
                        let gameId, game, nextState = createAndAdd state
                        reply.Reply((gameId, game))
                        return! messageLoop nextState

                    | TryCreate(maxGames, reply) ->
                        // Atomic cap check + create on a single mailbox turn (no TOCTOU).
                        let atCapacity =
                            match maxGames with
                            | Some m -> Map.count state >= m
                            | None -> false

                        if atCapacity then
                            reply.Reply(None)
                            return! messageLoop state
                        else
                            let gameId, game, nextState = createAndAdd state
                            reply.Reply(Some(gameId, game))
                            return! messageLoop nextState

                    | GetGame(gameId, reply) ->
                        match state |> Map.tryFind gameId with
                        | Some { Game = game } -> reply.Reply(Some game)
                        | None -> reply.Reply(None)

                        return! messageLoop state

                    | RemoveGame gameId ->
                        match Map.tryFind gameId state with
                        | Some gameRef ->
                            let nextState = state |> Map.remove gameId

                            try
                                gameRef.Subscription.Dispose()
                                gameRef.Game.Dispose()
                            with _ ->
                                ()

                            return! messageLoop nextState
                        | None -> return! messageLoop state

                    | Timeout ->
                        let cutoff = DateTimeOffset.UtcNow.AddHours(-1.0)

                        let removeGames, nextState =
                            state |> Map.partition (fun _ gameRef -> gameRef.Timestamp < cutoff)

                        for KeyValue(gameId, gameRef) in removeGames do
                            try
                                gameRef.Subscription.Dispose()
                                gameRef.Game.Dispose()
                            with _ ->
                                ()

                        return! messageLoop nextState

                    | Dispose ->
                        for KeyValue(_, gameRef) in state do
                            try
                                gameRef.Subscription.Dispose()
                                gameRef.Game.Dispose()
                            with _ ->
                                ()
                }

            messageLoop (Map<string, GameRef> Seq.empty))

    member private _.RemoveGame(gameId: string) = agent.Post(RemoveGame(gameId))

    member private _.UpdateState(gameId: string, result: MoveResult) = agent.Post(UpdateState(gameId, result))

    member private _.CleanupExpiredGames() = agent.Post(Timeout)

    interface GameSupervisor with
        member _.CreateGame() = agent.PostAndReply(CreateGame)

        member _.TryCreateGame(maxGames: int option) =
            agent.PostAndReply(fun reply -> TryCreate(maxGames, reply))

        member _.GetGame(gameId: string) =
            agent.PostAndReply(fun reply -> GetGame(gameId, reply))

        member _.GetActiveGameCount() = agent.PostAndReply(CountActive)

        member _.ListActiveGames() = agent.PostAndReply(ListGames)

        member _.SnapshotActiveGames() = agent.PostAndReply(Snapshot)

        member _.TryGetState(gameId: string) =
            agent.PostAndReply(fun reply -> GetStateOf(gameId, reply))

        member _.Dispose() =
            if not disposed then
                cleanupTimer.Dispose()
                agent.Post(Dispose)

let createGameSupervisor () : GameSupervisor =
    new GameSupervisorImpl() :> GameSupervisor
