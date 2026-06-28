module TicTacToe.OssDriver.Program

open TicTacToe.OssDriver.Types
open TicTacToe.OssDriver

// Drive ANY OpenAI-compatible model (OpenRouter/Groq/Together via WORKER_* env) as one
// seat of a curl arm. Run two instances (--role X / --role O) concurrently for a game.
//
//   dotnet run --project experiments/oss-driver -- \
//     --role X --base http://localhost:6228 --route games --game <id> --persona expert

let private argVal (argv: string[]) (name: string) (dflt: string) =
    match Array.tryFindIndex ((=) name) argv with
    | Some i when i + 1 < argv.Length -> argv.[i + 1]
    | _ -> dflt

[<EntryPoint>]
let main argv =
    let backend = Backend.autoDetect ()
    let role = argVal argv "--role" ""
    let baseUrl = argVal argv "--base" ""
    let route = argVal argv "--route" ""
    if role <> "X" && role <> "O" then eprintfn "--role X|O required"; exit 2
    if baseUrl = "" then eprintfn "--base <proxy url> required"; exit 2
    if route <> "games" && route <> "arenas" then eprintfn "--route games|arenas required"; exit 2

    let cfg: Driver.Config =
        { Backend = backend
          Model = argVal argv "--model" (LlmClient.defaultModel backend)
          Role = role
          Persona = Personas.get (argVal argv "--persona" "expert")
          Base = baseUrl
          Route = route
          Game = argVal argv "--game" ""
          ColdStart = Array.contains "--coldstart" argv
          MaxActions = argVal argv "--max-actions" "40" |> int
          MaxMoves = argVal argv "--max-moves" "12" |> int
          Window = argVal argv "--window" "8" |> int
          PollSeconds = argVal argv "--poll-seconds" "3" |> float }

    let result = Driver.run cfg
    printfn "%s" result
    0
