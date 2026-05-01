module TicTacToe.Orchestrator.Program

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open TicTacToe.Orchestrator.Types

let private usage = """
Usage: orchestrator run [options]

Options:
  --commit <sha>          git SHA or HEAD (default: HEAD)
  --variant <proto|simple> server variant (default: proto)
  --model <haiku|sonnet|opus> Claude model (default: haiku)
  --persona <beginner|expert|chaos> agent persona (default: beginner)
  --setup <E0|E1|E_RPC>   agent setup mode (default: E1)
  --games <N>             number of games (default: 3)
  --output <file>         output JSON file (default: run.json)
  --temperature <float>   sampling temperature (default: 0.0)
"""

let private parseArgs (args: string[]) : RunConfig option =
    let defaults : RunConfig = {
        Commit = "HEAD"; Variant = Proto; Model = Haiku; Persona = Beginner
        Setup = E1; Games = 3; Output = "run.json"; Temperature = 0.0
    }
    let rec parse (cfg: RunConfig) (args: string list) =
        match args with
        | [] -> Some cfg
        | "--commit" :: v :: rest -> parse { cfg with Commit = v } rest
        | "--variant" :: "proto" :: rest -> parse { cfg with Variant = Proto } rest
        | "--variant" :: "simple" :: rest -> parse { cfg with Variant = Simple } rest
        | "--model" :: "haiku" :: rest -> parse { cfg with Model = Haiku } rest
        | "--model" :: "sonnet" :: rest -> parse { cfg with Model = Sonnet } rest
        | "--model" :: "opus" :: rest -> parse { cfg with Model = Opus } rest
        | "--persona" :: "beginner" :: rest -> parse { cfg with Persona = Beginner } rest
        | "--persona" :: "expert" :: rest -> parse { cfg with Persona = Expert } rest
        | "--persona" :: "chaos" :: rest -> parse { cfg with Persona = Chaos } rest
        | "--setup" :: "E0" :: rest -> parse { cfg with Setup = E0 } rest
        | "--setup" :: "E1" :: rest -> parse { cfg with Setup = E1 } rest
        | "--setup" :: "E_RPC" :: rest -> parse { cfg with Setup = ERPC } rest
        | "--games" :: v :: rest ->
            match Int32.TryParse(v) with
            | true, n -> parse { cfg with Games = n } rest
            | _ -> None
        | "--output" :: v :: rest -> parse { cfg with Output = v } rest
        | "--temperature" :: v :: rest ->
            match Double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, t -> parse { cfg with Temperature = t } rest
            | _ -> None
        | unknown :: _ ->
            eprintfn "Unknown argument: %s" unknown
            None
    match args |> Array.toList with
    | "run" :: rest -> parse defaults rest
    | _ -> None

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.Converters.Add(JsonStringEnumConverter())
    opts

[<EntryPoint>]
let main args =
    match parseArgs args with
    | None ->
        printfn "%s" usage
        1
    | Some config ->
        let repoRoot =
            // Walk up from the executable to find the repo root (contains TicTacToe.sln)
            let rec find (dir: string) =
                if File.Exists(Path.Combine(dir, "TicTacToe.sln")) then dir
                else
                    let parent = Directory.GetParent(dir)
                    if parent = null then Directory.GetCurrentDirectory()
                    else find parent.FullName
            find (AppContext.BaseDirectory)

        let result = Runner.run config repoRoot |> Async.AwaitTask |> Async.RunSynchronously
        let json = JsonSerializer.Serialize(result, jsonOptions)
        File.WriteAllText(config.Output, json)
        printfn "Run complete. RPVA=%.2f, invalid_rate=%.2f, abandon_rate=%.2f"
            result.Aggregate.Rpva result.Aggregate.InvalidRate result.Aggregate.AbandonRate
        printfn "Output written to %s" config.Output
        0
