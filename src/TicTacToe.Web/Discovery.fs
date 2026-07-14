module TicTacToe.Web.Discovery

/// ALPS profile describing the app's affordances and their semantics (Sd).
let alpsProfile = """{
  "alps": {
    "version": "1.0",
    "doc": { "value": "Tic-tac-toe. m,n,k-game (3,3,3)." },
    "descriptor": [
      { "id": "take-seat", "type": "unsafe", "doc": { "value": "Claim the X or O seat by submitting a move; first mover on each side is seated." } },
      { "id": "make-move", "type": "unsafe", "rt": "#game", "doc": { "value": "POST player + position to /games/{id} (alias: /arenas/{id} — one resource, two names). player must be X or O. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight. Rejected if out of turn or square taken." } },
      { "id": "reset", "type": "idempotent", "doc": { "value": "POST /games/{id}/reset to reset the board and clear seats." } },
      { "id": "delete", "type": "idempotent", "doc": { "value": "DELETE /games/{id} (or POST /games/{id}/delete) to remove the game." } },
      { "id": "game", "type": "semantic", "doc": { "value": "A game resource: the board state plus whose turn it is." } }
    ]
  }
}"""

/// JSON Home document listing resources and relations (Sd).
let jsonHome = """{
  "resources": {
    "tag:tictactoe,2026:home": { "href": "/" },
    "tag:tictactoe,2026:game": { "href-template": "/games/{id}", "href-vars": { "id": "tag:tictactoe,2026:param;id" } },
    "tag:tictactoe,2026:profile": { "href": "/profile" }
  }
}"""

/// The game's RDF description as schema.org/Game JSON-LD (So). Absolute @id/#players URIs are
/// built from the request scheme+host so every named thing is a dereferenceable HTTP URI; zero
/// blank nodes; sameAs links to Wikidata + DBpedia.
let gameJsonLd (gameUri: string) =
    String.concat "\n" [
        "{"
        "  \"@context\": \"https://schema.org\","
        $"  \"@id\": \"{gameUri}\","
        "  \"@type\": \"Game\","
        "  \"name\": \"Tic-tac-toe\","
        "  \"description\": \"A two-player m,n,k (3,3,3) game: place three of your marks in a row to win.\","
        "  \"numberOfPlayers\": {"
        $"    \"@id\": \"{gameUri}#players\","
        "    \"@type\": \"QuantitativeValue\","
        "    \"value\": 2"
        "  },"
        "  \"sameAs\": ["
        "    \"http://www.wikidata.org/entity/Q210339\","
        "    \"http://dbpedia.org/resource/Tic-tac-toe\""
        "  ]"
        "}" ]
