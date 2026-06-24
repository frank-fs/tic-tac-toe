module TicTacToe.DiscoveryHarness.ColdStartTests

open Xunit
open TicTacToe.DiscoveryHarness

[<Theory>]
[<InlineData("tic-tac-toe")>]
[<InlineData("tic tac toe")>]
[<InlineData("/arenas")>]
[<InlineData("/games")>]
[<InlineData("position")>]
[<InlineData("TopLeft")>]
let ``discovery instruction reveals nothing app-specific`` (forbidden: string) =
    Assert.DoesNotContain(forbidden, ColdStart.discoveryInstruction.ToLowerInvariant())

[<Fact>]
let ``system prompt includes the base url and the report markers`` () =
    let p = ColdStart.systemPrompt "http://localhost:6328" (TicTacToe.OssDriver.Personas.get "expert")
    Assert.Contains("http://localhost:6328", p)
    Assert.Contains("DISCOVERY", p)
    Assert.Contains("ROLE", p)
