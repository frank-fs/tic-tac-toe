module TicTacToe.Web.Surface.Tests.RenderFactorTests

open System.IO
open Xunit
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Web.Surface.Surface
open TicTacToe.Web.Surface.Model
open TicTacToe.Web.Surface.templates.game

let private html (el: HtmlElement) =
    use sw = new StringWriter()
    (Render.toTextWriterAsync sw el).GetAwaiter().GetResult()
    sw.ToString()

// A fresh game is X's turn. Seat X as "userX", O as "userO".
let private freshXTurn () : MoveResult = startGame ()
let private seated =
    Some { GameId = "g"; PlayerXId = Some "userX"; PlayerOId = Some "userO" }

let private cell a c sd so = { A = a; C = c; Sd = sd; So = so }

[<Fact>]
let ``A0 renders nine move forms regardless of caller`` () =
    let out = renderArenaPage (cell false false false false) "g" (freshXTurn()) "observer" seated None |> html
    Assert.Equal(9, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``A1 gives the on-turn player their legal move forms`` () =
    let out = renderArenaPage (cell true false false false) "g" (freshXTurn()) "userX" seated None |> html
    // X to move on an empty board => 9 legal squares => 9 forms
    Assert.Equal(9, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``A1 gives the off-turn player no move forms`` () =
    let out = renderArenaPage (cell true false false false) "g" (freshXTurn()) "userO" seated None |> html
    Assert.Equal(0, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``A1 gives an observer no move forms`` () =
    let out = renderArenaPage (cell true false false false) "g" (freshXTurn()) "observer" seated None |> html
    Assert.Equal(0, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``C0 emits no grid role and no live region`` () =
    let out = renderArenaPage (cell false false false false) "g" (freshXTurn()) "userX" seated None |> html
    Assert.DoesNotContain("role=\"grid\"", out)
    Assert.DoesNotContain("aria-live", out)

[<Fact>]
let ``C1 emits grid role and a polite live region`` () =
    let out = renderArenaPage (cell false true false false) "g" (freshXTurn()) "userX" seated None |> html
    Assert.Contains("role=\"grid\"", out)
    Assert.Contains("role=\"gridcell\"", out)
    Assert.Contains("aria-live=\"polite\"", out)

[<Fact>]
let ``A1+C1 plain cells carry role=gridcell`` () =
    // userO is off-turn on fresh X-turn game => all 9 squares are plain (non-affordance) cells
    let out = renderArenaPage (cell true true false false) "g" (freshXTurn()) "userO" seated None |> html
    Assert.Equal(0, (out.Split("name=\"position\"").Length - 1))
    Assert.Contains("role=\"gridcell\"", out)
