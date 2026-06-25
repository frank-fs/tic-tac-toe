module TicTacToe.DiscoveryHarness.TranscriptTests

open Xunit
open TicTacToe.DiscoveryHarness.Transcript

[<Fact>]
let ``parses a DISCOVERY report line`` () =
    let line = "DISCOVERY {\"appIs\":\"tic-tac-toe\",\"goal\":\"win\",\"howToParticipate\":\"POST a move\"}"
    match tryParseDiscovery line with
    | Some d -> Assert.Equal("tic-tac-toe", d.AppIs); Assert.Equal("win", d.Goal)
    | None -> Assert.Fail "expected discovery"

[<Fact>]
let ``parses a ROLE report line`` () =
    match tryParseRole "ROLE {\"myRole\":\"observer\",\"myAffordances\":\"watch\",\"canIAct\":false}" with
    | Some r -> Assert.Equal("observer", r.MyRole); Assert.Equal(Some false, r.CanIAct)
    | None -> Assert.Fail "expected role"

[<Fact>]
let ``non-report line yields None`` () =
    Assert.True((tryParseDiscovery "GET /arenas/g1").IsNone)
