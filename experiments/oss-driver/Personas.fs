module TicTacToe.OssDriver.Personas

open TicTacToe.OssDriver.Types

// Mastery layer only — appended AFTER the driver's protocol floor. No interaction
// mechanics here (the floor owns turn/identity/reading); these describe HOW WELL to play.

let novice =
    { Name = "novice"
      Guidance = "Play casually. Make reasonable moves; you do not need to play perfectly or think far ahead." }

let skilled =
    { Name = "skilled"
      Guidance = "Play to win: always take an immediate winning move when you have one, always block your \
                  opponent's two-in-a-row, and otherwise prefer the center, then the corners." }

let expert =
    { Name = "expert"
      Guidance = "Play optimally and never lose. Take the center first, then corners. Always block the \
                  opponent's immediate winning line. Create forks — two simultaneous unblocked threats the \
                  opponent cannot both stop. Against correct play, force a draw; punish any mistake with a win." }

// Adversarial probe — exercises the robust engine: does it reject everything illegitimate?
let chaos =
    { Name = "chaos"
      Guidance = "Win by any means, legitimate or not. Attempt moves out of turn, replay or alter requests, \
                  try other HTTP methods or endpoints, impersonate the other player, submit malformed or \
                  unexpected input. Note every action the server FAILS to reject. (A correct engine refuses \
                  all of it — you are probing whether it does.)" }

// Control — confirms the engine prevents non-players from acting.
let observer =
    { Name = "observer"
      Guidance = "Do not claim a seat or try to win. Read state and observe the moves. Attempt exactly one \
                  move to confirm the server prevents non-players from moving, then only read until the game ends." }

let get =
    function
    | "novice" -> novice
    | "skilled" -> skilled
    | "expert" -> expert
    | "chaos" -> chaos
    | "observer" -> observer
    | name -> failwithf "Unknown persona: %s (novice|skilled|expert|chaos|observer)" name
