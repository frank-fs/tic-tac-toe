module TicTacToe.Orchestrator.Matrices.Smoke

open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Personas

// HTTP agents use the in-repo hypertext-faithful HTTP client (status + headers +
// held event streams), giving them the same protocol-native footing as ERPC's tools.
let private httpServer = {
    Name = "http"
    Command = "uv"
    Arguments = [| "run"; "--project"; "mcp-http"; "mcp-http" |]
}

let private mcpServer = {
    Name = "tictactoe-mcp"
    Command = "dotnet"
    Arguments = [| "run"; "--project"; "src/TicTacToe.Mcp/" |]
}

let private cell id variant p1 p2 p3 mcpServers = {
    Id = id
    Variant = variant
    Personas = (p1, p2, p3)
    Model = "google/gemma-4-e4b"
    InitialGames = 1
    MaxGames = 1
    MaxTurnsPerAgent = 50
    McpServers = mcpServers
    Temperature = 0.0
}

let smoke : CellSpec list = [
    cell "smoke-proto-bbb"  Proto  beginner beginner beginner [httpServer]
    cell "smoke-simple-bbb" Simple beginner beginner beginner [httpServer]
    cell "smoke-erpc-bbb"   ERPC   beginner beginner beginner [mcpServer]
    cell "smoke-erpc-bbc"   ERPC   beginner beginner chaos    [mcpServer]
    cell "smoke-proto-bbc"  Proto  beginner beginner chaos    [httpServer]
    cell "smoke-simple-bbc" Simple beginner beginner chaos    [httpServer]
]
