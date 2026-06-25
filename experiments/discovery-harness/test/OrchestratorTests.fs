module TicTacToe.DiscoveryHarness.OrchestratorTests

open Xunit
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

[<Fact>]
let ``realized seat reads X from role report`` () =
    let t = Transcript.empty "seatA" "expert" "m"
    t.Role <- Some { MyRole = "X"; MyAffordances = "move"; CanIAct = Some true }
    Assert.Equal("X", Orchestrator.realizedSeat t)

[<Fact>]
let ``no accepted move and no role => observer`` () =
    let t = Transcript.empty "seatC" "expert" "m"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g"; Body = None; Status = 403; BodySnippet = "NotAPlayer" }
    Assert.Equal("observer", Orchestrator.realizedSeat t)
