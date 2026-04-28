module TicTacToe.Orchestrator.Metrics

open TicTacToe.Orchestrator.Types

/// Compute per-game metrics from a completed game transcript.
/// totalTokens: total LLM context tokens consumed across all turns.
let computeMetrics (transcript: TranscriptEntry list) (totalTokens: int) : GameMetrics =
    let outcomes =
        transcript |> List.map (function
            | Http e -> e.Outcome
            | Tool e -> e.Outcome)

    let total = outcomes |> List.length |> float
    let validCount = outcomes |> List.filter ((=) ValidAction) |> List.length
    let invalidCount = outcomes |> List.filter ((=) InvalidAction) |> List.length

    let rpva =
        if validCount = 0 then System.Double.MaxValue
        else total / float validCount

    let invalidRate =
        if total = 0.0 then 0.0
        else float invalidCount / total

    { Rpva = rpva; InvalidRate = invalidRate; Abandoned = validCount = 0; Tokens = totalTokens }

/// Aggregate metrics across all games in a run.
let aggregate (games: GameRecord list) : Aggregate =
    let n = float (List.length games)
    if n = 0.0 then
        { Rpva = 0.0; InvalidRate = 0.0; AbandonRate = 0.0; Tokens = 0.0 }
    else
        let validRpvas = games |> List.map (fun g -> g.Metrics.Rpva) |> List.filter (fun r -> r < System.Double.MaxValue)
        let rpva =
            if validRpvas.IsEmpty then System.Double.MaxValue
            else validRpvas |> List.average
        let invalidRate = games |> List.averageBy (fun g -> g.Metrics.InvalidRate)
        let abandonRate = games |> List.filter (fun g -> g.Metrics.Abandoned) |> List.length |> fun c -> float c / n
        let tokens = games |> List.averageBy (fun g -> float g.Metrics.Tokens)
        { Rpva = rpva; InvalidRate = invalidRate; AbandonRate = abandonRate; Tokens = tokens }
