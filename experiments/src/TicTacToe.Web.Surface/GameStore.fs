module TicTacToe.Web.Surface.GameStore

open System
open System.Collections.Generic
open TicTacToe.Model

/// Messages for the GameStore MailboxProcessor
type GameStoreMsg =
    | Create of AsyncReplyChannel<(string * MoveResult) option>
    | Get    of string * AsyncReplyChannel<MoveResult option>
    | Update of string * Move * AsyncReplyChannel<MoveResult option>
    | Delete of string
    | Reset  of string * AsyncReplyChannel<MoveResult option>
    | List   of AsyncReplyChannel<(string * MoveResult) list>

/// MailboxProcessor-backed store imitating a database
/// No IObservable — purely request/response
type GameStore(?maxGames: int) =
    let agent =
        MailboxProcessor<GameStoreMsg>.Start(fun inbox ->
            let state = Dictionary<string, MoveResult>()

            let rec loop () =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Create reply ->
                        match maxGames with
                        | Some limit when state.Count >= limit ->
                            reply.Reply(None)
                        | _ ->
                            let id = Guid.NewGuid().ToString()
                            let result = startGame ()
                            state.[id] <- result
                            reply.Reply(Some(id, result))
                        return! loop ()

                    | Get(id, reply) ->
                        let result =
                            match state.TryGetValue(id) with
                            | true, r -> Some r
                            | false, _ -> None
                        reply.Reply(result)
                        return! loop ()

                    | Update(id, move, reply) ->
                        match state.TryGetValue(id) with
                        | true, current ->
                            // An invalid move yields Error but must NOT overwrite the live game:
                            // persisting it wedges the shared game (no transition out of Error),
                            // locking out both players. Mirror the Engine actor (Engine.fs MakeMove):
                            // report the rejection, keep the prior state.
                            let next = makeMove (current, move)
                            match next with
                            | Error _ -> ()
                            | _ -> state.[id] <- next
                            reply.Reply(Some next)
                        | false, _ ->
                            reply.Reply(None)
                        return! loop ()

                    | Delete id ->
                        state.Remove(id) |> ignore
                        return! loop ()

                    | Reset(id, reply) ->
                        match state.ContainsKey(id) with
                        | true ->
                            let fresh = startGame ()
                            state.[id] <- fresh
                            reply.Reply(Some fresh)
                        | false ->
                            reply.Reply(None)
                        return! loop ()

                    | List reply ->
                        let entries = state |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq
                        reply.Reply(entries)
                        return! loop ()
                }

            loop ())

    member _.Create() : (string * MoveResult) option =
        agent.PostAndReply(fun ch -> Create ch)

    member _.Get(id: string) =
        agent.PostAndReply(fun ch -> Get(id, ch))

    member _.Update(id: string, move: Move) =
        agent.PostAndReply(fun ch -> Update(id, move, ch))

    member _.Delete(id: string) =
        agent.Post(Delete id)

    member _.Reset(id: string) =
        agent.PostAndReply(fun ch -> Reset(id, ch))

    member _.List() =
        agent.PostAndReply(fun ch -> List ch)

    /// The hard game cap (None = unlimited). Used to decide whether to offer creation.
    member _.MaxGames = maxGames
