module TicTacToe.McpRpc.Identity

open System
open TicTacToe.Model

/// Per-connection authenticated identity. On stdio one process == one
/// connection == one agent, so a single instance is the connection's session.
type SessionIdentity() =
    let mutable token: string option = None

    /// Mint a new identity token for this connection and return it.
    member _.Authenticate() : string =
        let t = Guid.NewGuid().ToString("N")
        token <- Some t
        t

    /// The currently authenticated token, if any.
    member _.Current: string option = token
