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

let private drive (argv: string[]) : int =
    let arm = argVal argv "--arm" "http"
    let backend = Backend.autoDetect ()
    let role = argVal argv "--role" ""
    let baseUrl = argVal argv "--base" ""
    let route = argVal argv "--route" ""
    let multiplayer = Array.contains "--multiplayer" argv   // ERPC: run all 3 seats over one shared connection
    if not multiplayer && role <> "X" && role <> "O" then eprintfn "--role X|O required"; exit 2
    if arm = "http" then
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
          MaxAttempts = argVal argv "--max-attempts" "30" |> int
          MaxTurns = argVal argv "--max-turns" "80" |> int
          MaxMoves = argVal argv "--max-moves" "12" |> int
          Window = argVal argv "--window" "8" |> int
          PollSeconds = argVal argv "--poll-seconds" "3" |> float }

    let result =
        if arm = "erpc" then (if multiplayer then ErpcDriver.runMultiplayer cfg else ErpcDriver.run cfg)
        else Driver.run cfg
    printfn "%s" result
    0

// Subcommands fold the whole measurement path into the one driver (one language, one path):
//   proxy    — logging reverse proxy (was proxy.py)
//   friction — classify request friction (was friction.py)
//   grade    — score a discovery run vs a per-cell ground truth (was grade.py)
//   code     — behavior-code a seat transcript (browser-user vs API-client conduct)
//   (quality — RELOCATED, spec 003b: the minimax scorer is now the sample plugin at
//    experiment/scorer/, loaded by the harness `quality` subcommand as an IQualityScorer.)
// Anything else (or a leading --flag) is the default: drive a model as one seat.
[<EntryPoint>]
let main argv =
    match Array.tryHead argv with
    | Some "proxy" -> Proxy.run argv.[1..]
    | Some "friction" -> Friction.run argv.[1..]
    | Some "grade" -> Grader.run argv.[1..]
    | Some "code" -> Coder.run argv.[1..]
    | Some "erpc-smoke" when argv.Length > 1 -> McpClient.smoke argv.[1] (System.IO.Directory.GetCurrentDirectory())
    | Some "erpc-http-smoke" when argv.Length > 1 -> McpClient.smokeHttp argv.[1] (System.IO.Directory.GetCurrentDirectory())
    | _ -> drive argv
