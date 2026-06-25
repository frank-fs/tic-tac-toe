module TicTacToe.DiscoveryHarness.OrchestratorTests

open Xunit
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

let private withAccepted seat =
    let t = Transcript.empty seat "expert" "m"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g"; Body = Some "player=X&position=TopLeft"; Status = 200; BodySnippet = "" }
    t

let private observerLike seat =
    let t = Transcript.empty seat "expert" "m"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g"; Body = None; Status = 403; BodySnippet = "You are not a player in this arena." }
    t

[<Fact>]
let ``arrival order: first accepted = X, second = O, none = observer`` () =
    let seats = Orchestrator.groundTruthSeats [ withAccepted "seatA"; withAccepted "seatB"; observerLike "seatC" ]
    Assert.Equal<string list>([ "X"; "O"; "observer" ], seats)

[<Fact>]
let ``no accepted moves => all observers`` () =
    let seats = Orchestrator.groundTruthSeats [ observerLike "seatA"; observerLike "seatB"; observerLike "seatC" ]
    Assert.Equal<string list>([ "observer"; "observer"; "observer" ], seats)

[<Fact>]
let ``ground truth ignores agent self-report: second seater is O even if it claims X`` () =
    // Regression guard for the observed bug: the second player self-reported "X" and
    // every party got labeled X. Ground truth = arrival order, so it must be O.
    let t2 = withAccepted "seatB"
    t2.Role <- Some { MyRole = "X"; MyAffordances = "move"; CanIAct = Some true }
    let seats = Orchestrator.groundTruthSeats [ withAccepted "seatA"; t2; observerLike "seatC" ]
    Assert.Equal<string list>([ "X"; "O"; "observer" ], seats)
