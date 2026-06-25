module TicTacToe.DiscoveryHarness.HtmlBoard

open System.Text.RegularExpressions

let positions =
    [| "TopLeft"; "TopCenter"; "TopRight"
       "MiddleLeft"; "MiddleCenter"; "MiddleRight"
       "BottomLeft"; "BottomCenter"; "BottomRight" |]

let private cellAt (html: string) (pos: string) : string option =
    let m = Regex.Match(html, "aria-label=\"" + Regex.Escape pos + "\"[^>]*>\\s*([^<\\s]*)\\s*<")
    if not m.Success then None
    else
        match m.Groups.[1].Value with
        | "X" -> Some "X"
        | "O" -> Some "O"
        | _ -> Some ""

let parse (html: string) : string[] option =
    let cells = positions |> Array.map (cellAt html)
    if cells |> Array.forall Option.isSome then Some(cells |> Array.map Option.get) else None
