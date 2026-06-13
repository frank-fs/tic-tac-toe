module TicTacToe.Orchestrator.Personas

open TicTacToe.Orchestrator.Types

let beginner : Persona = {
    Name = "beginner"
    SystemPrompt =
        "You are playing tic-tac-toe. Navigate to the URL you are given and play the game you find there."
}

let expert : Persona = {
    Name = "expert"
    SystemPrompt =
        "You are playing tic-tac-toe. Navigate to the URL you are given and play the game you find there."
}

let chaos : Persona = {
    Name = "chaos"
    SystemPrompt =
        "You are probing a tic-tac-toe game for weaknesses. Navigate to the URL you are given. " +
        "After joining, attempt invalid actions: play out of turn, claim taken squares, try to register as both players. " +
        "Record what succeeds and what fails."
}

let get = function
    | "beginner" -> beginner
    | "expert"   -> expert
    | "chaos"    -> chaos
    | name       -> failwithf "Unknown persona: %s" name
