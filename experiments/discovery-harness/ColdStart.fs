module TicTacToe.DiscoveryHarness.ColdStart

open TicTacToe.OssDriver.Types

// FROZEN cold-start contract. Names no app specifics (no game, role, path, move
// format). Identity is owned by the driver's cookie jar — not coached here. Only the
// abstract goal + how to talk back to the harness.
let discoveryInstruction =
    "You are an autonomous agent. You are given ONLY a base URL. Nothing about what \
     lives there has been described to you. Your job: review the application at that \
     URL and interact with it to accomplish whatever goal it presents.\n\n\
     Work in two stages.\n\n\
     STAGE 1 — DISCOVER (read-only). Issue read requests to learn what this is. When \
     you can, reply with EXACTLY one line of JSON and nothing else:\n\
     DISCOVERY {\"appIs\":\"...\",\"goal\":\"...\",\"howToParticipate\":\"...\"}\n\n\
     STAGE 2 — PARTICIPATE. Take part and pursue the goal. The system that assigns you \
     a part may accept or refuse your attempts; learn your part from how it responds. \
     Once you know it, reply with one line of JSON and nothing else:\n\
     ROLE {\"myRole\":\"...\",\"myAffordances\":\"...\",\"canIAct\":true|false}\n\n\
     ACTIONS. Every other reply is EXACTLY one HTTP request, one line, nothing else:\n\
     \  GET /path\n\
     or\n\
     \  POST /path key=value&key2=value2\n\n\
     I run the request and return its status + body. Pace yourself; it is not a race. \
     If the server goes quiet after activity, the task has likely ended — stop."

let systemPrompt (baseUrl: string) (persona: Persona) : string =
    sprintf "%s\n\nBase URL: %s\n\nHow well to pursue the goal: %s\n\nBegin Stage 1."
        discoveryInstruction baseUrl persona.Guidance
