module TicTacToe.Orchestrator.Program

open System
open System.IO
open TicTacToe.Orchestrator.Orchestrator
open TicTacToe.Orchestrator.Matrices.Smoke

[<EntryPoint>]
let main args =
    let repoRoot =
        let rec find (dir: string) =
            if File.Exists(Path.Combine(dir, "TicTacToe.sln")) then dir
            else
                let parent = Directory.GetParent(dir)
                if parent = null then failwith "Cannot find repo root (TicTacToe.sln not found)"
                find parent.FullName
        find (Directory.GetCurrentDirectory())

    let matrix, cells =
        match args with
        | [| "smoke" |] -> "smoke", smoke
        | [| "proto-ab" |] -> "proto-ab", protoAb
        | [| name |] -> failwithf "Unknown matrix: %s. Available: smoke, proto-ab" name
        | _ -> failwith "Usage: dotnet run --project experiments/orchestrator/ -- <matrix-name>"

    runMatrix repoRoot matrix cells |> Async.RunSynchronously
    0
