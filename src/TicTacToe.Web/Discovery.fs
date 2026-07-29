module TicTacToe.Web.Discovery

/// ALPS profile describing the app's affordances and their semantics (Sd). Fields are decomposed
/// into their own semantic descriptors and referenced by `href` from the action/game descriptors --
/// not just named in prose -- so a client can enumerate the vocabulary structurally instead of
/// parsing English out of a doc string.
let alpsProfile = """{
  "alps": {
    "version": "1.0",
    "doc": { "value": "Tic-tac-toe. m,n,k-game (3,3,3)." },
    "descriptor": [
      { "id": "player", "type": "semantic", "doc": { "value": "Which side is acting: X or O." } },
      { "id": "position", "type": "semantic", "doc": { "value": "One of the nine named squares." },
        "descriptor": [
          { "id": "TopLeft" }, { "id": "TopCenter" }, { "id": "TopRight" },
          { "id": "MiddleLeft" }, { "id": "MiddleCenter" }, { "id": "MiddleRight" },
          { "id": "BottomLeft" }, { "id": "BottomCenter" }, { "id": "BottomRight" }
        ] },
      { "id": "square-state", "type": "semantic", "doc": { "value": "A square's contents: X, O, or empty." } },
      { "id": "board", "type": "semantic", "doc": { "value": "The nine squares, each named by position, holding a square-state." },
        "descriptor": [ { "href": "#position" }, { "href": "#square-state" } ] },
      { "id": "turn", "type": "semantic", "doc": { "value": "Whose move it is next, or the terminal outcome (a player has won, or the board is a draw)." },
        "descriptor": [ { "href": "#player" } ] },
      { "id": "game", "type": "semantic", "doc": { "value": "A game resource: the board state plus whose turn it is." },
        "descriptor": [ { "href": "#board" }, { "href": "#turn" } ] },
      { "id": "take-seat", "type": "unsafe", "rt": "#game",
        "doc": { "value": "Claim the X or O seat by submitting the first move for that side; first mover on each side is seated. This is the same wire action as make-move (below), not a separate endpoint -- it is only distinguished by game state (no seat yet taken for that side)." },
        "descriptor": [ { "href": "#player" }, { "href": "#position" } ] },
      { "id": "make-move", "type": "unsafe", "rt": "#game",
        "doc": { "value": "POST player + position to /games/{id}. Rejected if out of turn or the square is already taken." },
        "descriptor": [ { "href": "#player" }, { "href": "#position" } ] },
      { "id": "reset", "type": "idempotent", "rt": "#game", "doc": { "value": "POST /games/{id}/reset to reset the board and clear seats." } },
      { "id": "delete", "type": "idempotent", "doc": { "value": "DELETE /games/{id} (or POST /games/{id}/delete) to remove the game." } }
    ]
  }
}"""

/// JSON Home document listing resources and relations (Sd).
let jsonHome = """{
  "resources": {
    "tag:tictactoe,2026:home": { "href": "/" },
    "tag:tictactoe,2026:game": { "href-template": "/games/{id}", "href-vars": { "id": "tag:tictactoe,2026:param;id" } },
    "tag:tictactoe,2026:profile": { "href": "/.well-known/alps.json" }
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
