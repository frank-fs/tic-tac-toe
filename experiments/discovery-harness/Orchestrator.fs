module TicTacToe.DiscoveryHarness.Orchestrator

open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open TicTacToe.OssDriver.Types
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

type RunConfig =
    { Backend: Backend; Model: string; Persona: Persona; Base: string
      MaxActions: int; MaxMoves: int; Window: int; PollSeconds: float }

[<Literal>]
let private gateTimeoutMs = 120000

let private seatCfg (rc: RunConfig) label gate signal : Driver.SeatConfig =
    { Backend = rc.Backend; Model = rc.Model; Seat = label; Persona = rc.Persona; Base = rc.Base
      MaxActions = rc.MaxActions; MaxMoves = rc.MaxMoves; Window = rc.Window; PollSeconds = rc.PollSeconds
      StartGate = gate; SeatedSignal = signal }

let realizedSeat (t: Transcript) : string =
    let m = t.Role |> Option.map (fun r -> r.MyRole.ToLowerInvariant())
    match m with
    | Some s when s.Contains "observ" || s.Contains "spectat" || s.Contains "watch" -> "observer"
    | Some s when s.Contains "x" -> "X"
    | Some s when s.Contains "o" -> "O"
    | _ -> if t.Requests |> Seq.exists (fun r -> r.Method = "POST" && r.Status < 400) then "player" else "observer"

let runGame (rc: RunConfig) : Transcript list =
    // Plain `let` (not `use`): a timeout task below may call Set() after runGame
    // returns; disposing the gates would turn that into an ObjectDisposedException.
    let gateB = new ManualResetEventSlim(false)
    let gateC = new ManualResetEventSlim(false)
    let cts = new CancellationTokenSource()
    let cfgA = seatCfg rc "seatA" None (Some(fun () -> gateB.Set()))
    let cfgB = seatCfg rc "seatB" (Some gateB) (Some(fun () -> gateC.Set()))
    let cfgC = seatCfg rc "seatC" (Some gateC) None
    // Bound (R10): release each gate after the timeout UNLESS the game ends first —
    // cts.Cancel wakes the wait so the timeout task exits instead of lingering.
    let timeout (g: ManualResetEventSlim) =
        Task.Run(fun () -> if not (cts.Token.WaitHandle.WaitOne gateTimeoutMs) then g.Set())
    timeout gateB |> ignore
    timeout gateC |> ignore
    let results =
        [ cfgA; cfgB; cfgC ] |> List.map (fun c -> Task.Run(fun () -> Driver.runSeat c)) |> List.map (fun t -> t.Result)
    cts.Cancel()
    results

let resultsJson (rc: RunConfig) (transcripts: Transcript list) : string =
    let parties = JsonArray()
    for t in transcripts do
        let realized = realizedSeat t
        let g = Grader.grade { t with Seat = realized }
        let p = JsonObject()
        p["seat"] <- JsonValue.Create realized
        p["recognize_appIs"] <- JsonValue.Create g.Recognize.AppIsHit
        p["recognize_goal"] <- JsonValue.Create g.Recognize.GoalHit
        p["recognize_multiplayer"] <- JsonValue.Create g.Recognize.MultiplayerHit
        p["role_named"] <- JsonValue.Create g.Recognize.RoleNamed
        p["role_discrimination"] <- JsonValue.Create g.Recognize.RoleDiscriminationCorrect
        p["first_action_coherent"] <- JsonValue.Create g.Recognize.FirstActionCoherent
        p["accepted_moves"] <- JsonValue.Create g.AcceptedMoves
        p["rejected_moves"] <- JsonValue.Create g.RejectedMoves
        p["rejection_codes"] <- JsonValue.Create (String.concat "," g.RejectionCodes)
        p["outcome"] <- JsonValue.Create g.Outcome
        p["blunders"] <- JsonValue.Create g.Blunders
        p["moves_scored"] <- JsonValue.Create g.MovesScored
        p["actions"] <- JsonValue.Create g.Actions
        parties.Add p
    let root = JsonObject()
    root["model"] <- JsonValue.Create rc.Model
    root["persona"] <- JsonValue.Create rc.Persona.Name
    root["base"] <- JsonValue.Create rc.Base
    root["parties"] <- parties
    root.ToJsonString()
