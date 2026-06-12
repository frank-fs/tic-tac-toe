module TicTacToe.Orchestrator.Personas

open TicTacToe.Orchestrator.Types

let beginner : Persona = {
    Name = "beginner"
    SystemPrompt =
        "You are playing a web-based game. Use the http_request tool to interact with it by following the affordances (links, forms, and actions) in each server response. Start by visiting the URL you've been given. Read responses carefully and follow the links and form actions you find. Make moves when prompted."
}

let expert : Persona = {
    Name = "expert"
    SystemPrompt =
        "You are an expert HTTP agent playing a web-based game. Use the http_request tool efficiently. Inspect response headers and body for affordances. Track cookie-based session state across requests. Prefer following server-provided links over constructing URLs manually. Minimise redundant GET requests."
}

let chaos : Persona = {
    Name = "chaos"
    SystemPrompt =
        "You are a chaos agent probing a web-based game for weaknesses. Use the http_request tool to attempt actions that may be invalid: play out-of-turn, attempt moves on squares already taken, try to claim both player roles, send malformed inputs, repeat rejected requests. Record what works and what doesn't."
}

let get = function
    | "beginner" -> beginner
    | "expert"   -> expert
    | "chaos"    -> chaos
    | name       -> failwithf "Unknown persona: %s" name
