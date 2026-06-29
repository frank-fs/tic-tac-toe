module TicTacToe.OssDriver.McpClient

// Launch the ERPC MCP server (mcp-rpc) as a stdio subprocess and expose list/call, so the
// ONE driver can drive the RPC null-hypothesis arm too. The model calls these tools; identity
// is the model's (authenticate once -> pass identityToken on make_move), same discipline as the
// HTTP cold-start. stdout is the JSON-RPC channel; the server logs to stderr.

open System
open System.Collections.Generic
open System.Text.Json
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

let private noArgs : IReadOnlyDictionary<string, obj> = readOnlyDict ([]: (string * obj) list)

type Erpc(serverDll: string, workdir: string) =
    let transport =
        new StdioClientTransport(
            StdioClientTransportOptions(
                Name = "erpc",
                Command = "dotnet",
                Arguments = [| serverDll |],
                WorkingDirectory = workdir))
    let client = McpClient.CreateAsync(transport).GetAwaiter().GetResult()

    member _.Tools : IList<McpClientTool> = client.ListToolsAsync().GetAwaiter().GetResult()

    member _.Call (name: string) (args: IReadOnlyDictionary<string, obj>) : string =
        let r = client.CallToolAsync(name, args).GetAwaiter().GetResult()
        r.Content
        |> Seq.choose (fun c -> match c with | :? TextContentBlock as t -> Some t.Text | _ -> None)
        |> String.concat "\n"

    interface IDisposable with
        member _.Dispose() = (try (client :> IAsyncDisposable).DisposeAsync().AsTask().Wait() with _ -> ())

/// No-LLM smoke: prove the client can list + call tools (authenticate -> new_game -> make_move).
let smoke (serverDll: string) (workdir: string) : int =
    use e = new Erpc(serverDll, workdir)
    eprintfn "=== tools ==="
    for t in e.Tools do eprintfn "  %s — %s" t.Name t.Description
    let prop (s: string) (k: string) = JsonDocument.Parse(s).RootElement.GetProperty(k).GetString()
    let token = prop (e.Call "authenticate" noArgs) "token"
    eprintfn "token=%s" token
    let gid = prop (e.Call "new_game" noArgs) "gameId"
    eprintfn "gameId=%s" gid
    let mv = e.Call "make_move" (readOnlyDict [ "gameId", box gid; "position", box "MiddleCenter"; "identityToken", box token ])
    eprintfn "make_move(MiddleCenter) -> %s" mv
    0
