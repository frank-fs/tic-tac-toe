module TicTacToe.McpRpc.Program

open System.Security.Claims
open System.Threading
open System.Text.Json.Nodes
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.McpRpc.Identity

let private configureLogging (builder: HostApplicationBuilder) =
    // All logs go to stderr; stdout is reserved for MCP JSON-RPC communication
    builder.Logging.AddConsole(fun opts -> opts.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

/// Read the per-request identity token from the incoming request's `_meta`.
/// The orchestrator injects it as params._meta.identityToken on every tools/call.
/// `JsonRpcRequest.Params` is the raw JsonNode of the request params; the MCP
/// `RequestParams.Meta` field is wire-named "_meta", so we read it directly.
let private metaToken (msg: JsonRpcMessage) : string option =
    match msg with
    | :? JsonRpcRequest as req ->
        match req.Params with
        | null -> None
        | parms ->
            match parms.["_meta"] with
            | :? JsonObject as meta ->
                match meta.["identityToken"] with
                | null -> None
                | t -> t.GetValue<string>() |> Option.ofObj
            | _ -> None
    | _ -> None

/// Bridge: on every incoming request, project the per-request identity token
/// (from `_meta.identityToken`) onto context.User as a ClaimsPrincipal so tools
/// can read identity via the injected ClaimsPrincipal. No per-connection state —
/// identity travels per request, set by the orchestrator. This lets one shared
/// stdio connection carry many distinct identities across interleaved requests.
///
/// context.User is ALWAYS set to a non-null principal. With a token, its identity
/// carries it as ClaimTypes.Name. Without one, an empty identity (no Name) is used
/// so the SDK still injects a non-null ClaimsPrincipal — without this, an unset
/// User makes the SDK fall back to DI resolution for the `ClaimsPrincipal`
/// parameter and throw an opaque framework error. The empty principal lets
/// make_move's token derivation yield None -> clean "unauthenticated".
let private bridgeIdentity (next: McpMessageHandler) : McpMessageHandler =
    McpMessageHandler(fun (context: MessageContext) (ct: CancellationToken) ->
        let identity =
            match metaToken context.JsonRpcMessage with
            | Some token ->
                ClaimsIdentity([ Claim(ClaimTypes.Name, token) ], "MetaAuth", ClaimTypes.Name, ClaimTypes.Role)
            | None -> ClaimsIdentity()

        context.User <- ClaimsPrincipal(identity)

        next.Invoke(context, ct))

[<EntryPoint>]
let main _ =
    let builder = Host.CreateApplicationBuilder()
    configureLogging builder

    builder.Services.AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor ()) |> ignore
    builder.Services.AddSingleton<PlayerAssignmentStore>() |> ignore

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithMessageFilters(fun filters ->
            filters.AddIncomingFilter(McpMessageFilter(bridgeIdentity)) |> ignore)
        .WithTools<Tools.TicTacToeTools>()
    |> ignore

    builder.Build().Run()
    0
