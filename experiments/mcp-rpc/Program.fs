module TicTacToe.McpRpc.Program

open System.Security.Claims
open System.Threading
open System.Text.Json.Nodes
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.McpRpc.Identity
open TicTacToe.McpRpc.EventLog

let private configureLogging (builder: HostApplicationBuilder) =
    // All logs go to stderr; stdout is reserved for MCP JSON-RPC communication
    builder.Logging.AddConsole(fun opts -> opts.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

/// Read the per-request identity token from the incoming request's `_meta`.
/// The orchestrator injects it as params._meta.identityToken on every tools/call.
/// `JsonRpcRequest.Params` is the raw JsonNode of the request params; the MCP
/// `RequestParams.Meta` field is wire-named "_meta", so we read it directly.
/// Returns None for any request shape that does not carry a present string token —
/// absent _meta, missing identityToken, or a non-string identityToken value all
/// degrade to anonymous rather than throwing.
let private metaToken (msg: JsonRpcMessage) : string option =
    match msg with
    | :? JsonRpcRequest as req ->
        match req.Params with
        | null -> None
        | parms ->
            match parms.["_meta"] with
            | :? JsonObject as meta ->
                match meta.["identityToken"] with
                | :? JsonValue as jv ->
                    let mutable value = ""
                    if jv.TryGetValue<string>(&value) then Some value else None
                | _ -> None
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

/// Shared DI: the same singletons back both transports so game state, seat assignment, the login registry
/// and the event log persist for the whole run regardless of how clients connect.
let private registerServices (services: IServiceCollection) =
    services.AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor ()) |> ignore
    services.AddSingleton<PlayerAssignmentStore>() |> ignore
    services.AddSingleton<Tools.ToolState>() |> ignore
    services.AddSingleton<EventLog>(fun _ ->
        match System.Environment.GetEnvironmentVariable("TICTACTOE_REQUEST_LOG_PATH") with
        | null | "" -> EventLog()
        | path -> EventLog(path))
    |> ignore

/// stdio host: one client, one session (the single-seat discovery arm). Identity flows per-request via the
/// _meta bridge, unchanged.
let private runStdio () =
    let builder = Host.CreateApplicationBuilder()
    configureLogging builder
    registerServices builder.Services
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithMessageFilters(fun filters ->
            filters.AddIncomingFilter(McpMessageFilter(bridgeIdentity)) |> ignore)
        .WithTools<Tools.TicTacToeTools>()
    |> ignore
    builder.Build().Run()

/// HTTP host: MANY clients, each its own session (the multiplayer arm). Stateful transport gives one
/// McpServer per session, so authenticate() binds identity to the session — no _meta bridge needed.
let private runHttp (url: string) =
    let builder = WebApplication.CreateBuilder()
    builder.Logging.AddConsole(fun opts -> opts.LogToStandardErrorThreshold <- LogLevel.Trace) |> ignore
    builder.WebHost.UseUrls(url) |> ignore
    registerServices builder.Services
    builder.Services.AddMcpServer().WithHttpTransport().WithTools<Tools.TicTacToeTools>() |> ignore
    let app = builder.Build()
    app.MapMcp() |> ignore
    app.Run()

[<EntryPoint>]
let main argv =
    match Array.tryFindIndex ((=) "--http") argv with
    | Some i when i + 1 < argv.Length -> runHttp argv.[i + 1]
    | _ -> runStdio ()
    0
