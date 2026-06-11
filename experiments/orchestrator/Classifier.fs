module TicTacToe.Orchestrator.Classifier

open TicTacToe.Orchestrator.Types

/// Classify an HTTP response outcome.
/// priorRequests: list of (method, url) pairs seen in this game so far.
/// Retry wins if this exact (method, url) pair was already issued.
/// Then: 4xx → InvalidAction, GET → Discovery, else → ValidAction.
let classifyOutcome (method: string) (url: string) (statusCode: int) (priorRequests: (string * string) list) : OutcomeTag =
    let isRetry = priorRequests |> List.exists (fun (m, u) -> m = method && u = url)
    if isRetry then Retry
    elif statusCode >= 400 then InvalidAction
    elif method = "GET" then Discovery
    else ValidAction

/// Classify the strategy used to produce this URL.
/// priorResponseBodies: all response body strings received so far in this game.
/// urlInOpenApiDoc: true if the URL path appears in the OpenAPI document fetched this game.
let classifyStrategy (method: string) (url: string) (priorResponseBodies: string list) (urlInOpenApiDoc: bool) : StrategyTag =
    let path = System.Uri(url).AbsolutePath
    let appearsInBody =
        priorResponseBodies |> List.exists (fun body ->
            body.Contains(url)
            || body.Contains(path)
            || body.Contains(sprintf "@post('%s')" path)
            || body.Contains(sprintf "@get('%s')" path)
            || body.Contains(sprintf "@put('%s')" path)
            || body.Contains(sprintf "@delete('%s')" path))
    if appearsInBody then HtmlFollow
    elif urlInOpenApiDoc then SpecFollow
    else BlindPost
