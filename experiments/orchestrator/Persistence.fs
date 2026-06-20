module TicTacToe.Orchestrator.Persistence

open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open TicTacToe.Orchestrator.Types

let private jsonOptions =
    let opts = JsonSerializerOptions(WriteIndented = true)
    opts.Converters.Add(JsonStringEnumConverter())
    opts

let private writeJson (path: string) (value: 'a) =
    let json = JsonSerializer.Serialize(value, jsonOptions)
    File.WriteAllText(path, json)

let private cellSpecToJson (cell: CellSpec) =
    let (p1, p2, p3) = cell.Personas
    {| id = cell.Id
       variant = Variant.toString cell.Variant
       personas = [| p1.Name; p2.Name; p3.Name |]
       model = cell.Model
       initial_games = cell.InitialGames
       max_games = cell.MaxGames
       max_turns_per_agent = cell.MaxTurnsPerAgent
       temperature = cell.Temperature |}

/// Move any prior results for this run's cells (+ the matrix manifest) into
/// results/archive/<stamp>/ so a fresh run never appends to a stale log or reads a
/// previous run's leftovers. No-op for names with no existing directory.
let archivePriorRun (repoRoot: string) (matrixName: string) (cellIds: string list) (stamp: string) =
    let resultsDir = Path.Combine(repoRoot, "experiments", "results")
    let archiveDir = Path.Combine(resultsDir, "archive", stamp)
    let move (name: string) =
        let src = Path.Combine(resultsDir, name)
        if Directory.Exists src then
            Directory.CreateDirectory archiveDir |> ignore
            Directory.Move(src, Path.Combine(archiveDir, name))
    (matrixName :: cellIds) |> List.iter move

let saveCell (repoRoot: string) (cellId: string) (result: CellResult) =
    let cellDir = Path.Combine(repoRoot, "experiments", "results", cellId)
    let transcriptsDir = Path.Combine(cellDir, "transcripts")
    Directory.CreateDirectory(transcriptsDir) |> ignore

    for kvp in result.Transcripts do
        let path = Path.Combine(transcriptsDir, $"{kvp.Key}.json")
        writeJson path kvp.Value

    writeJson (Path.Combine(cellDir, "metrics.json")) result.Metrics
    writeJson (Path.Combine(cellDir, "cell-spec.json")) (cellSpecToJson result.CellSpec)

let saveManifest (repoRoot: string) (matrixName: string) (cells: CellSpec list) (results: CellResult list) =
    let manifestDir = Path.Combine(repoRoot, "experiments", "results", matrixName)
    Directory.CreateDirectory(manifestDir) |> ignore
    let manifest = {|
        matrix = matrixName
        cells = results |> List.map (fun r -> {|
            cell_id = r.CellSpec.Id
            outcome = r.Metrics.Outcome
            completion_signal = r.Metrics.CompletionSignal
        |})
    |}
    writeJson (Path.Combine(manifestDir, "manifest.json")) manifest
