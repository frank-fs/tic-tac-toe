module TicTacToe.DiscoveryHarness.OptimalTests

open Xunit
open TicTacToe.DiscoveryHarness

let private board (s: string) = s.ToCharArray() |> Array.map (fun c -> if c = '.' then "" else string c)

[<Fact>]
let ``winner detects a row`` () =
    Assert.Equal("X", Optimal.winner (board "XXX...OO."))

[<Fact>]
let ``no winner on empty`` () =
    Assert.Equal("", Optimal.winner (board "........."))

[<Fact>]
let ``taking the win is not a blunder`` () =
    Assert.False(Optimal.isBlunder (board "XX..O.O..") "X" 2)

[<Fact>]
let ``missing the win is a blunder`` () =
    Assert.True(Optimal.isBlunder (board "XX..O.O..") "X" 3)

[<Fact>]
let ``failing to block loses — blunder`` () =
    Assert.True(Optimal.isBlunder (board "XX..O....") "O" 5)
    Assert.False(Optimal.isBlunder (board "XX..O....") "O" 2)
