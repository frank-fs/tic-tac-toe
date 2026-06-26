module TicTacToe.Web.Surface.Discovery

/// ALPS profile describing the app's affordances and their semantics.
let alpsProfile = """{
  "alps": {
    "version": "1.0",
    "doc": { "value": "Tic-tac-toe arena. m,n,k-game (3,3,3)." },
    "descriptor": [
      { "id": "take-seat", "type": "unsafe", "doc": { "value": "Claim the X or O seat by submitting a move; first mover on each side is seated." } },
      { "id": "make-move", "type": "unsafe", "rt": "#arena", "doc": { "value": "POST player + position to /arenas/{id}; rejected if out of turn or square taken." } },
      { "id": "restart", "type": "idempotent", "doc": { "value": "POST /arenas/{id}/restart to reset the board and clear seats." } },
      { "id": "delete", "type": "idempotent", "doc": { "value": "DELETE /arenas/{id} (or POST /arenas/{id}/delete) to remove the arena." } },
      { "id": "arena", "type": "semantic", "doc": { "value": "An arena resource: the board state plus whose turn it is." } }
    ]
  }
}"""

/// JSON Home document listing resources and relations.
let jsonHome = """{
  "resources": {
    "tag:tictactoe,2026:home": { "href": "/" },
    "tag:tictactoe,2026:arena": { "href-template": "/arenas/{id}", "href-vars": { "id": "tag:tictactoe,2026:param;id" } },
    "tag:tictactoe,2026:profile": { "href": "/profile" }
  }
}"""
