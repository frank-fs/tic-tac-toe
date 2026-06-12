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
            agent.PostAndReply(GetState)

        member _.Dispose() =
            if not disposed then
                disposed <- true
                agent.Post(Stop)

    interface IObservable<MoveResult> with
        member _.Subscribe(observer) =
            agent.PostAndReply(fun reply -> Subscribe(observer, reply))

let createGame () : Game = new GameImpl() :> Game

type GameSupervisor =
    inherit IDisposable
    abstract CreateGame: unit -> string * Game
    abstract GetGame: gameId: string -> Game option
    abstract GetActiveGameCount: unit -> int
    abstract ListActiveGames: unit -> string list

type GameRef =
    { Game: Game
      Subscription: IDisposable
      Timestamp: DateTimeOffset }

type GameSupervisorMessage =
    | CountActive of AsyncReplyChannel<int>
    | ListGames of AsyncReplyChannel<string list>
    | CreateGame of AsyncReplyChannel<string * Game>
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

                    | CreateGame reply ->
                        let gameId = Guid.NewGuid().ToString()
                        let game = createGame ()
                        let timestamp = DateTimeOffset.UtcNow

                        let subscription =
                            game.Subscribe(
                                { new IObserver<MoveResult> with
                                    member _.OnNext(_) = ()
                                    member _.OnCompleted() = this.RemoveGame(gameId)
                                    member _.OnError(_) = this.RemoveGame(gameId) }
                            )

                        let gameRef =
                            { Game = game
                              Timestamp = timestamp
                              Subscription = subscription }

                        let nextState = state |> Map.add gameId gameRef

                        reply.Reply((gameId, game))

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

    member private _.CleanupExpiredGames() = agent.Post(Timeout)

    interface GameSupervisor with
        member _.CreateGame() = agent.PostAndReply(CreateGame)

        member _.GetGame(gameId: string) =
            agent.PostAndReply(fun reply -> GetGame(gameId, reply))

        member _.GetActiveGameCount() = agent.PostAndReply(CountActive)

        member _.ListActiveGames() = agent.PostAndReply(ListGames)

        member _.Dispose() =
            if not disposed then
                cleanupTimer.Dispose()
                agent.Post(Dispose)

let createGameSupervisor () : GameSupervisor =
    new GameSupervisorImpl() :> GameSupervisor
