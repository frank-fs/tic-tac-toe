module TicTacToe.DiscoveryHarness.ColdStartTests

open Xunit
open TicTacToe.DiscoveryHarness

// Tokens are lowercase: the instruction is compared via ToLowerInvariant(), so a
// mixed-case token (e.g. "TopLeft") would match nothing and the case would be vacuous.
[<Theory>]
[<InlineData("tic-tac-toe")>]
[<InlineData("tic tac toe")>]
[<InlineData("/arenas")>]
[<InlineData("/games")>]
[<InlineData("position")>]
[<InlineData("topleft")>]
[<InlineData("multiplayer")>]
let ``discovery instruction reveals nothing app-specific`` (forbidden: string) =
    Assert.DoesNotContain(forbidden, ColdStart.discoveryInstruction.ToLowerInvariant())

[<Fact>]
let ``system prompt includes the base url and the report markers`` () =
    let p = ColdStart.systemPrompt "http://localhost:6328" (TicTacToe.OssDriver.Personas.get "expert")
    Assert.Contains("http://localhost:6328", p)
    Assert.Contains("DISCOVERY", p)
    Assert.Contains("ROLE", p)
