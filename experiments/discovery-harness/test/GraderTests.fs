module TicTacToe.DiscoveryHarness.GraderTests

open Xunit
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

let private t0 seat = Transcript.empty seat "expert" "test"

[<Fact>]
let ``recognizes tic-tac-toe and multiplayer`` () =
    let t = t0 "X"
    t.Discovery <- Some { AppIs = "a two-player tic-tac-toe game"; Goal = "three in a row to win"
                          HowToParticipate = "post a move against your opponent" }
    let s = Grader.grade t
    Assert.True(s.Recognize.AppIsHit); Assert.True(s.Recognize.GoalHit); Assert.True(s.Recognize.MultiplayerHit)

[<Fact>]
let ``observer that cannot act scores role-discrimination`` () =
    let t = t0 "observer"
    t.Role <- Some { MyRole = "observer"; MyAffordances = "watch"; CanIAct = Some false }
    Assert.True((Grader.grade t).Recognize.RoleDiscriminationCorrect)

[<Fact>]
let ``observer claiming it can act fails role-discrimination`` () =
    let t = t0 "observer"
    t.Role <- Some { MyRole = "observer"; MyAffordances = "watch"; CanIAct = Some true }
    Assert.False((Grader.grade t).Recognize.RoleDiscriminationCorrect)

[<Fact>]
let ``rejected NotAPlayer move tallied with code`` () =
    let t = t0 "observer"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g1"; Body = Some "player=X&position=TopLeft"
                     Status = 403; BodySnippet = "Rejected NotAPlayer" }
    let s = Grader.grade t
    Assert.Equal(0, s.AcceptedMoves); Assert.Equal(1, s.RejectedMoves); Assert.Contains("NotAPlayer", s.RejectionCodes)

[<Fact>]
let ``a blundered accepted move is counted`` () =
    let t = t0 "X"
    // Board before move: X can win at index 2 (TopRight). Agent plays index 3 instead (blunder).
    t.Boards.Add { AfterRequestIndex = 0; Cells = [| "X";"X";"";"";"O";"";"O";"";"" |] }
    t.Requests.Add { Method = "GET"; Path = "/arenas/g1"; Body = None; Status = 200; BodySnippet = "" }      // index 0
    t.Requests.Add { Method = "POST"; Path = "/arenas/g1"; Body = Some "player=X&position=MiddleLeft"        // index 1
                     Status = 200; BodySnippet = "" }
    let s = Grader.grade t
    Assert.Equal(1, s.MovesScored); Assert.Equal(1, s.Blunders)
