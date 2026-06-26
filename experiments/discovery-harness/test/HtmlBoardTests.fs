module TicTacToe.DiscoveryHarness.HtmlBoardTests

open Xunit
open TicTacToe.DiscoveryHarness

// Real bytes captured from TicTacToe.Web.Surface (port 5328, TICTACTOE_INITIAL_GAMES=1).
// X posted to TopLeft; O posted to MiddleCenter; remaining 7 squares are empty.
// Empty cells render as &#183; (HTML entity for middle dot U+00B7).
// Disabled squares: class="square" disabled="disabled"; clickable: class="square square-clickable".
// Forms include hidden inputs for player/position before the button.
let private fixture = """<div class="board"><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="TopLeft"><button class="square" type="submit" aria-label="TopLeft" disabled="disabled">X</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="TopCenter"><button class="square square-clickable" type="submit" aria-label="TopCenter">&#183;</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="TopRight"><button class="square square-clickable" type="submit" aria-label="TopRight">&#183;</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="MiddleLeft"><button class="square square-clickable" type="submit" aria-label="MiddleLeft">&#183;</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="MiddleCenter"><button class="square" type="submit" aria-label="MiddleCenter" disabled="disabled">O</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="MiddleRight"><button class="square square-clickable" type="submit" aria-label="MiddleRight">&#183;</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="BottomLeft"><button class="square square-clickable" type="submit" aria-label="BottomLeft">&#183;</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="BottomCenter"><button class="square square-clickable" type="submit" aria-label="BottomCenter">&#183;</button></form><form method="post" action="/arenas/g1"><input type="hidden" name="player" value="X"><input type="hidden" name="position" value="BottomRight"><button class="square square-clickable" type="submit" aria-label="BottomRight">&#183;</button></form></div>"""

[<Fact>]
let ``parses X O and empties in board order`` () =
    match HtmlBoard.parse fixture with
    | Some cells ->
        // Full 9-cell assertion: any positional shift / off-by-one corrupts the
        // grader's blunder scorer, so check every slot, not a sample.
        Assert.Equal<string[]>([| "X"; ""; ""; ""; "O"; ""; ""; ""; "" |], cells)
    | None -> Assert.Fail "expected a parsed board"

[<Fact>]
let ``non-board html yields None`` () =
    Assert.True((HtmlBoard.parse "<html>nope</html>").IsNone)
