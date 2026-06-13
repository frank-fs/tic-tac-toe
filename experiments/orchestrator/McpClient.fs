module TicTacToe.Orchestrator.McpClient

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open TicTacToe.Orchestrator.LlmClient
open TicTacToe.Orchestrator.Types

let private toolDefFromMcpTool (t: McpClientTool) : ToolDef =
    let schemaNode =
        try JsonNode.Parse(t.JsonSchema.GetRawText())
        with _ -> JsonObject() :> JsonNode
    { Name = t.Name
      Description = t.Description |> Option.ofObj |> Option.defaultValue ""
      InputSchema = schemaNode }

let private extractText (result: CallToolResult) : string =
    result.Content
    |> Seq.tryPick (function
        | :? TextContentBlock as tb -> Some tb.Text
        | _ -> None)
    |> Option.defaultValue ""

[<Literal>]
let private toolCallTimeoutSeconds = 120.0

type McpConnection(client: McpClient, tools: ToolDef list) =
    member _.Tools = tools
    // ct is the per-cell cancellation; on cell-cap cancel it propagates (not swallowed)
    // so the agent aborts the turn. The internal timeout still returns a tool_timeout result.
    member _.CallToolAsync(name: string, args: Map<string, JsonNode>, ct: CancellationToken) : Task<string> =
        task {
            let argsDict = Dictionary<string, obj>()
            for kvp in args do
                argsDict.[kvp.Key] <- kvp.Value :> obj
            use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds toolCallTimeoutSeconds)
            use linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            try
                let! result =
                    client.CallToolAsync(
                        name,
                        argsDict :> IReadOnlyDictionary<string, obj>,
                        cancellationToken = linked.Token)
                return extractText result
            with :? OperationCanceledException when not ct.IsCancellationRequested ->
                return $"""{{ "error": "tool_timeout: {name} exceeded {toolCallTimeoutSeconds}s" }}"""
        }
    interface IDisposable with
        member _.Dispose() = client.DisposeAsync().AsTask().GetAwaiter().GetResult()

type McpClientSet(configs: McpServerConfig list) =
    let mutable connections: McpConnection list = []

    member _.InitializeAsync(ct: CancellationToken) : Task =
        task {
            if not (List.isEmpty connections) then
                failwith "McpClientSet.InitializeAsync called more than once"
            for cfg in configs do
                let opts = StdioClientTransportOptions(
                    Name = cfg.Name,
                    Command = cfg.Command,
                    Arguments = cfg.Arguments)
                let transport = StdioClientTransport(opts)
                let! client = McpClient.CreateAsync(transport, cancellationToken = ct)
                let! toolList = client.ListToolsAsync(cancellationToken = ct)
                let tools = toolList |> Seq.map toolDefFromMcpTool |> Seq.toList
                connections <- McpConnection(client, tools) :: connections
        } :> Task

    member _.GetAllTools() : ToolDef list =
        connections |> List.collect (fun c -> c.Tools)

    member _.CallToolAsync(name: string, args: Map<string, JsonNode>, ct: CancellationToken) : Task<string> =
        task {
            let conn =
                connections
                |> List.tryFind (fun c -> c.Tools |> List.exists (fun t -> t.Name = name))
            match conn with
            | None -> return $"""{{ "error": "tool_not_found: {name}" }}"""
            | Some c -> return! c.CallToolAsync(name, args, ct)
        }

    interface IDisposable with
        member _.Dispose() =
            for c in connections do (c :> IDisposable).Dispose()
