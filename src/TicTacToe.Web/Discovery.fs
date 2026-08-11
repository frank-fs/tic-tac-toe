module TicTacToe.Web.Discovery

open Frank.Alps

/// ALPS profile describing the app's affordances and their semantics (Sd). Fields are decomposed
/// into their own semantic descriptors and referenced by `href` from the action/game descriptors --
/// not just named in prose -- so a client can enumerate the vocabulary structurally instead of
/// parsing English out of a doc string.
let player =
    descriptor "player" {
        semantic
        doc "Which side is acting."
        contains [ DescriptorFunctions.semantic "X"; DescriptorFunctions.semantic "O" ]
    }

let position =
    descriptor "position" {
        semantic
        doc "One of the nine named squares."
        contains [
            DescriptorFunctions.semantic "TopLeft"; DescriptorFunctions.semantic "TopCenter"; DescriptorFunctions.semantic "TopRight"
            DescriptorFunctions.semantic "MiddleLeft"; DescriptorFunctions.semantic "MiddleCenter"; DescriptorFunctions.semantic "MiddleRight"
            DescriptorFunctions.semantic "BottomLeft"; DescriptorFunctions.semantic "BottomCenter"; DescriptorFunctions.semantic "BottomRight" ]
    }

let squareState =
    descriptor "square-state" {
        semantic
        doc "A square's contents: taken by a player, or empty."
        contains [ descriptor "square-state-player" { semantic; href player }; DescriptorFunctions.semantic "empty" ]
    }

let board =
    descriptor "board" {
        semantic
        doc "The nine squares, each named by position, holding a square-state."
        contains [
            descriptor "board-position" { semantic; href position }
            descriptor "board-square-state" { semantic; href squareState } ]
    }

let outcome =
    descriptor "outcome" {
        semantic
        doc "The terminal result of a finished game: a player has won, or the board is a draw."
        contains [ descriptor "outcome-player" { semantic; href player }; DescriptorFunctions.semantic "draw" ]
    }

let turn =
    descriptor "turn" {
        semantic
        doc "Whose move it is next, or the terminal outcome (a player has won, or the board is a draw)."
        contains [
            descriptor "turn-player" { semantic; href player }
            descriptor "turn-outcome" { semantic; href outcome } ]
    }

let game =
    descriptor "game" {
        semantic
        doc "A game resource: the board state plus whose turn it is."
        contains [ descriptor "game-board" { semantic; href board }; descriptor "game-turn" { semantic; href turn } ]
    }

let createGame =
    descriptor "create-game" {
        unsafe
        rt game
        doc "POST /games to create a new game. The server assigns the id and returns 201 Created with a Location header pointing at it."
    }

let viewGame =
    descriptor "view-game" {
        safe
        rt game
        doc "GET /games/{id} — the current board state and whose turn it is, or the terminal outcome once the game is decided."
    }

let takeSeat =
    descriptor "take-seat" {
        unsafe
        rt game
        doc "Claim the X or O seat by submitting the first move for that side; first mover on each side is seated. This is the same wire action as make-move (below), not a separate endpoint -- it is only distinguished by game state (no seat yet taken for that side)."
        contains [
            descriptor "take-seat-player" { semantic; href player }
            descriptor "take-seat-position" { semantic; href position } ]
    }

let makeMove =
    descriptor "make-move" {
        unsafe
        rt game
        doc "POST player + position to /games/{id}. Rejected if out of turn or the square is already taken."
        contains [
            descriptor "make-move-player" { semantic; href player }
            descriptor "make-move-position" { semantic; href position } ]
    }

/// `unsafe`, not `idempotent`: each POST creates a brand-new game resource (Handlers.resetGame
/// issues a fresh gameId), so repeating the call is not idempotent in ALPS's sense. Frank.Alps's
/// startup `binds` validation (idempotent -> PUT/DELETE only) would fail host startup on the POST
/// binding in Program.fs if this stayed "idempotent" -- that check is what surfaced this mislabel
/// in the original hand-rolled profile.
let reset =
    descriptor "reset" {
        unsafe
        rt game
        doc "POST /games/{id}/reset to reset the board and clear seats."
    }

let delete =
    descriptor "delete" {
        idempotent
        doc "DELETE /games/{id} (or POST /games/{id}/delete) to remove the game."
    }

let alpsProfile : Descriptor list =
    [ player; position; squareState; board; outcome; turn; game; createGame; viewGame; takeSeat; makeMove; reset; delete ]

open Frank.Rdf
open TicTacToe.Model

/// The game's RDF description as schema.org/Game JSON-LD (So). Absolute IRIs are built from the
/// request scheme+host so every named thing is a dereferenceable HTTP URI; zero blank nodes;
/// sameAs links to Wikidata + DBpedia. Two triples with the same (subject, predicate) and
/// different objects (the two sameAs targets) is exactly how RDF represents a multi-valued
/// property -- Doc.writeJsonLd's expanded form renders that as a JSON array for the predicate.
let private staticGameDoc (gameUri: string) : Doc =
    let game = Node.Iri gameUri
    let players = Node.Iri $"{gameUri}#players"

    rdf {
        prefix "schema" "https://schema.org/"

        about (
            describe game {
                typ "schema:Game"
                propertyString "schema:name" "Tic-tac-toe"
                propertyString "schema:description" "A two-player m,n,k (3,3,3) game: place three of your marks in a row to win."
                propertyNode "schema:numberOfPlayers" players
                propertyNode "schema:sameAs" (Node.Iri "http://www.wikidata.org/entity/Q210339")
                propertyNode "schema:sameAs" (Node.Iri "http://dbpedia.org/resource/Tic-tac-toe")
            }
        )

        about (describe players { typ "schema:QuantitativeValue"; propertyInt "schema:value" 2 })
    }

/// So+Sd coupling, one square: predicates are the exact fragment ids `board` (line 33 above) declares
/// for its own nested descriptors -- `alpsBase` names the running server's own /.well-known/alps.json,
/// so `alps:board-position`/`alps:board-square-state` dereference to real ALPS descriptors, not minted
/// vocabulary. Empty squares carry no information here (closed-world: the board's 9 positions are a
/// known-fixed set) so only Taken squares get a node -- an RDF client sees exactly the occupied cells.
let private squareDoc (alpsBase: string) (gameUri: string) (pos: SquarePosition) (occupant: Player) : Doc =
    let square = Node.Iri $"{gameUri}#square-{pos}"

    rdf {
        prefix "alps" (alpsBase + "#")

        about (
            describe square {
                propertyString "alps:board-position" (string pos)
                propertyString "alps:board-square-state" (string occupant)
            }
        )
    }

let private boardOf (result: MoveResult) : GameState =
    match result with
    | XTurn(state, _)
    | OTurn(state, _)
    | Won(state, _)
    | Draw state
    | Error(state, _) -> state

/// One `squareDoc` per occupied square, folded into a single Doc via Doc.merge -- bounded to 9
/// iterations by the board's fixed size (Holzmann R10), so a plain List.fold replaces what would
/// otherwise need a loop inside the `rdf { }` CE (which has no For/Combine to support one).
let private boardDoc (alpsBase: string) (gameUri: string) (result: MoveResult) : Doc =
    [ for KeyValue(pos, square) in boardOf result do
        match square with
        | Taken occupant -> squareDoc alpsBase gameUri pos occupant
        | Empty -> Doc.Empty ]
    |> List.fold Doc.merge Doc.Empty

/// So+Sd coupling: whose turn it is, or the terminal outcome -- reuses `turn`/`outcome` (the same
/// top-level ALPS ids declared above) as the predicate, matching Discovery.alpsProfile's own doc text
/// ("turn ... or the terminal outcome"): a game in progress gets `alps:turn`, a finished one gets
/// `alps:outcome`, never both.
let private turnOutcomeDoc (alpsBase: string) (gameUri: string) (result: MoveResult) : Doc =
    let game = Node.Iri gameUri
    let triple predicate value =
        rdf {
            prefix "alps" (alpsBase + "#")
            about (describe game { propertyString predicate value })
        }

    match result with
    | XTurn _ -> triple "alps:turn" "X"
    | OTurn _ -> triple "alps:turn" "O"
    | Won(_, winner) -> triple "alps:outcome" (string winner)
    | Draw _ -> triple "alps:outcome" "draw"
    | Error _ -> Doc.Empty

/// The game's RDF description as schema.org/Game JSON-LD (So), optionally coupled to the ALPS profile
/// (Sd): `alpsProfileUri` is `Some` only when Sd is also on (see Handlers.gameType), so the two
/// factors stay independent -- So alone still gets just `staticGameDoc`'s class-level description.
/// When both are on, the game resource also gets `dcterms:conformsTo` the profile, plus live
/// board/turn/outcome triples expressed with the ALPS profile's own fragment IRIs as predicates
/// (`boardDoc`/`turnOutcomeDoc`) -- a client dereferencing those predicates lands on the running
/// server's own ALPS descriptors, not documentation prose.
let gameDoc (gameUri: string) (alpsProfileUri: string option) (state: MoveResult option) : Doc =
    let baseDoc = staticGameDoc gameUri

    match alpsProfileUri with
    | None -> baseDoc
    | Some alpsBase ->
        let conformsDoc =
            rdf {
                prefix "dcterms" "http://purl.org/dc/terms/"
                about (describe (Node.Iri gameUri) { propertyNode "dcterms:conformsTo" (Node.Iri alpsBase) })
            }

        let stateDoc =
            match state with
            | Some result -> Doc.merge (turnOutcomeDoc alpsBase gameUri result) (boardDoc alpsBase gameUri result)
            | None -> Doc.Empty

        baseDoc |> Doc.merge conformsDoc |> Doc.merge stateDoc
