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

type McpConnection(client: McpClient, rawTools: McpClientTool list, tools: ToolDef list) =
    member _.Tools = tools

    // Identity travels in MCP `_meta`, never in tool args (no LLM visibility, not
    // forgeable by the model). McpClientTool.WithMeta(meta) returns a meta-bound tool
    // whose CallAsync sets RequestOptions.Meta, serialized as the request's `_meta`
    // field (verified against ModelContextProtocol.Core 1.2.0). identityToken=None
    // keeps the plain path for non-ERPC arms.
    member _.CallToolAsync(name: string, args: Map<string, JsonNode>, identityToken: string option, ct: CancellationToken) : Task<string> =
        task {
            let argsDict = Dictionary<string, obj>()
            for kvp in args do
                argsDict.[kvp.Key] <- kvp.Value :> obj
            use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds toolCallTimeoutSeconds)
            use linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            try
                let! result =
                    match identityToken with
                    | Some token ->
                        let meta = JsonObject()
                        meta.["identityToken"] <- JsonValue.Create token
                        let tool =
                            rawTools
                            |> List.tryFind (fun t -> t.Name = name)
                            |> Option.defaultWith (fun () -> failwith $"identity-scoped call for unknown tool: {name}")
                        (tool.WithMeta(meta).CallAsync(
                            argsDict :> IReadOnlyDictionary<string, obj>,
                            cancellationToken = linked.Token)).AsTask()
                    | None ->
                        (client.CallToolAsync(
                            name,
                            argsDict :> IReadOnlyDictionary<string, obj>,
                            cancellationToken = linked.Token)).AsTask()
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
                let rawTools = toolList |> Seq.toList
                let tools = rawTools |> List.map toolDefFromMcpTool
                connections <- McpConnection(client, rawTools, tools) :: connections
        } :> Task

    member _.GetAllTools() : ToolDef list =
        connections |> List.collect (fun c -> c.Tools)

    member this.CallToolAsync(name: string, args: Map<string, JsonNode>, ct: CancellationToken) : Task<string> =
        this.CallToolAsync(name, args, None, ct)

    // Identity-scoped route: the routed connection carries the agent's token in `_meta`.
    member _.CallToolAsync(name: string, args: Map<string, JsonNode>, identityToken: string option, ct: CancellationToken) : Task<string> =
        task {
            let conn =
                connections
                |> List.tryFind (fun c -> c.Tools |> List.exists (fun t -> t.Name = name))
            match conn with
            | None -> return $"""{{ "error": "tool_not_found: {name}" }}"""
            | Some c -> return! c.CallToolAsync(name, args, identityToken, ct)
        }

    interface IDisposable with
        member _.Dispose() =
            for c in connections do (c :> IDisposable).Dispose()
