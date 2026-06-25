module TicTacToe.DiscoveryHarness.DriverTests

open Xunit
open TicTacToe.DiscoveryHarness

[<Fact>]
let ``parses a GET action`` () =
    Assert.Equal(Some("GET","/arenas/g1",None), Driver.parseAction "GET /arenas/g1")

[<Fact>]
let ``parses a POST with body`` () =
    Assert.Equal(Some("POST","/arenas/g1",Some "player=X&position=TopLeft"),
                 Driver.parseAction "POST /arenas/g1 player=X&position=TopLeft")

[<Fact>]
let ``parses GET of the bare root`` () =
    // Cold-start agents must be able to fetch the base URL root; the path is "/".
    Assert.Equal(Some("GET","/",None), Driver.parseAction "GET /")
