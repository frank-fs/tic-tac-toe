module TicTacToe.McpRpc.Program

open System.Security.Claims
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.McpRpc.Identity

let private configureLogging (builder: HostApplicationBuilder) =
    // All logs go to stderr; stdout is reserved for MCP JSON-RPC communication
    builder.Logging.AddConsole(fun opts -> opts.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

/// Bridge: on every incoming MCP message, read the connection's authenticated
/// token from SessionIdentity (set by the authenticate tool) and project it onto
/// context.User as a ClaimsPrincipal so tools can read identity via injected
/// ClaimsPrincipal. SessionIdentity is a Singleton, so the token persists across
/// the separate authenticate and make_move calls on the same stdio connection.
let private bridgeIdentity (next: McpMessageHandler) : McpMessageHandler =
    McpMessageHandler(fun (context: MessageContext) (ct: CancellationToken) ->
        let session = context.Services.GetService<SessionIdentity>()

        match (if isNull (box session) then None else session.Current) with
        | Some token ->
            let identity =
                ClaimsIdentity([ Claim(ClaimTypes.Name, token) ], "StdioAuth", ClaimTypes.Name, ClaimTypes.Role)

            context.User <- ClaimsPrincipal(identity)
        | None -> ()

        next.Invoke(context, ct))

[<EntryPoint>]
let main _ =
    let builder = Host.CreateApplicationBuilder()
    configureLogging builder

    builder.Services.AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor ()) |> ignore
    builder.Services.AddSingleton<SessionIdentity>() |> ignore
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
