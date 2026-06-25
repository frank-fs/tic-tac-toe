module TicTacToe.DiscoveryHarness.Program

open System.IO
open TicTacToe.OssDriver.Types
open TicTacToe.OssDriver
open TicTacToe.DiscoveryHarness

let private argVal (argv: string[]) name dflt =
    match Array.tryFindIndex ((=) name) argv with
    | Some i when i + 1 < argv.Length -> argv.[i + 1]
    | _ -> dflt

[<EntryPoint>]
let main argv =
    let backend = Backend.autoDetect ()
    let baseUrl = argVal argv "--base" ""
    if baseUrl = "" then eprintfn "--base <proxy url> required"; exit 2
    let rc: Orchestrator.RunConfig =
        { Backend = backend
          Model = argVal argv "--model" (LlmClient.defaultModel backend)
          Persona = Personas.get (argVal argv "--persona" "expert")
          Base = baseUrl
          MaxActions = argVal argv "--max-actions" "40" |> int
          MaxMoves = argVal argv "--max-moves" "12" |> int
          Window = argVal argv "--window" "10" |> int
          PollSeconds = argVal argv "--poll-seconds" "3" |> float }
    let json = Orchestrator.resultsJson rc (Orchestrator.runGame rc)
    let out = argVal argv "--out" ""
    if out <> "" then File.WriteAllText(out, json)
    printfn "%s" json
    0
