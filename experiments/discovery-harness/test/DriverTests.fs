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
