module TicTacToe.Mcp.Program

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Mcp.Tools

[<EntryPoint>]
let main _args =
    let builder = Host.CreateApplicationBuilder()

    builder.Logging.AddConsole(fun opts ->
        opts.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    builder.Services.AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor()) |> ignore
    builder.Services.AddSingleton<GameTools>() |> ignore

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<GameTools>()
    |> ignore

    builder.Build().Run()
    0
