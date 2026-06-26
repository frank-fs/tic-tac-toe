module TicTacToe.Web.Surface.Tests.SurfaceParseTests

open Xunit
open TicTacToe.Web.Surface.Surface

[<Fact>]
let ``parse 0000 is the floor`` () =
    Assert.Equal({ A = false; C = false; Sd = false; So = false }, parse "0000")

[<Fact>]
let ``parse 1111 is all factors on`` () =
    Assert.Equal({ A = true; C = true; Sd = true; So = true }, parse "1111")

[<Fact>]
let ``parse 1010 maps positionally to A and Sd`` () =
    Assert.Equal({ A = true; C = false; Sd = true; So = false }, parse "1010")

[<Fact>]
let ``parse rejects wrong length`` () =
    Assert.Throws<exn>(fun () -> parse "101" |> ignore) |> ignore

[<Fact>]
let ``parse rejects non-binary chars`` () =
    Assert.Throws<exn>(fun () -> parse "12ab" |> ignore) |> ignore

[<Fact>]
let ``parse rejects empty`` () =
    Assert.Throws<exn>(fun () -> parse "" |> ignore) |> ignore
