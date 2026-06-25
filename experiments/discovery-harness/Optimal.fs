module TicTacToe.DiscoveryHarness.Optimal

let private lines = [| (0,1,2);(3,4,5);(6,7,8);(0,3,6);(1,4,7);(2,5,8);(0,4,8);(2,4,6) |]

let winner (cells: string[]) : string =
    lines |> Array.tryPick (fun (a,b,c) ->
        if cells.[a] <> "" && cells.[a] = cells.[b] && cells.[b] = cells.[c] then Some cells.[a] else None)
    |> Option.defaultValue ""

let private other = function "X" -> "O" | _ -> "X"
let private score = function "X" -> 1 | "O" -> -1 | _ -> 0

// Minimax to absolute value (X=+1, O=-1, draw=0). Bounded by empty cells (≤9).
let rec private minimax (cells: string[]) (toMove: string) : int =
    let w = winner cells
    if w <> "" then score w
    else
        let empties = [ for i in 0..8 do if cells.[i] = "" then yield i ]
        if List.isEmpty empties then 0
        else
            let vals = empties |> List.map (fun i ->
                let n = Array.copy cells in n.[i] <- toMove; minimax n (other toMove))
            if toMove = "X" then List.max vals else List.min vals

let private bestValue (cells: string[]) (mover: string) : int =
    let empties = [ for i in 0..8 do if cells.[i] = "" then yield i ]
    let vals = empties |> List.map (fun i ->
        let n = Array.copy cells in n.[i] <- mover; minimax n (other mover))
    if List.isEmpty vals then 0 elif mover = "X" then List.max vals else List.min vals

let isBlunder (cells: string[]) (mover: string) (chosenIndex: int) : bool =
    if chosenIndex < 0 || chosenIndex > 8 || cells.[chosenIndex] <> "" then false
    else
        let n = Array.copy cells in n.[chosenIndex] <- mover
        let chosen = minimax n (other mover)
        let best = bestValue cells mover
        if mover = "X" then chosen < best else chosen > best
