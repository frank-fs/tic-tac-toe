module TicTacToe.McpRpc.Program

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server

[<EntryPoint>]
let main _ =
    let builder = Host.CreateApplicationBuilder()

    // All logs go to stderr; stdout is reserved for MCP JSON-RPC communication
    builder.Logging.AddConsole(fun opts ->
        opts.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<Tools.TicTacToeTools>()
    |> ignore

    builder.Build().Run()
    0
