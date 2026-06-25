module TicTacToe.DiscoveryHarness.Transcript

open System.Text.Json.Nodes
open System.Text.RegularExpressions

type ReqRecord = { Method: string; Path: string; Body: string option; Status: int; BodySnippet: string }
type DiscoveryReport = { AppIs: string; Goal: string; HowToParticipate: string }
type RoleReport = { MyRole: string; MyAffordances: string; CanIAct: bool option }
type BoardSnapshot = { AfterRequestIndex: int; Cells: string[] }

type Transcript =
    { Seat: string; Persona: string; Model: string
      Requests: ResizeArray<ReqRecord>
      mutable Discovery: DiscoveryReport option
      mutable Role: RoleReport option
      Boards: ResizeArray<BoardSnapshot>
      mutable Outcome: string; mutable Tokens: int; mutable Actions: int; mutable MovesSubmitted: int }

let empty seat persona model =
    { Seat = seat; Persona = persona; Model = model
      Requests = ResizeArray(); Discovery = None; Role = None; Boards = ResizeArray()
      Outcome = "incomplete"; Tokens = 0; Actions = 0; MovesSubmitted = 0 }

let private str (o: JsonObject) k =
    let mutable v : JsonNode = null
    match o.TryGetPropertyValue(k, &v) with
    | true when v <> null -> v.GetValue<string>()
    | _ -> ""

let private boolOpt (o: JsonObject) k =
    let mutable v : JsonNode = null
    match o.TryGetPropertyValue(k, &v) with
    | true when v <> null -> (try Some(v.GetValue<bool>()) with _ -> None)
    | _ -> None

let private extract (prefix: string) (line: string) : JsonObject option =
    let m = Regex.Match(line, prefix + @"\s*(\{.*\})")
    if not m.Success then None
    else try Some(JsonNode.Parse(m.Groups.[1].Value) :?> JsonObject) with _ -> None

let tryParseDiscovery line =
    extract "DISCOVERY" line
    |> Option.map (fun o -> { AppIs = str o "appIs"; Goal = str o "goal"; HowToParticipate = str o "howToParticipate" })

let tryParseRole line =
    extract "ROLE" line
    |> Option.map (fun o -> { MyRole = str o "myRole"; MyAffordances = str o "myAffordances"; CanIAct = boolOpt o "canIAct" })
