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
    [ player; position; squareState; board; outcome; turn; game; takeSeat; makeMove; reset; delete ]

open Frank.Rdf

/// The game's RDF description as schema.org/Game JSON-LD (So). Absolute IRIs are built from the
/// request scheme+host so every named thing is a dereferenceable HTTP URI; zero blank nodes;
/// sameAs links to Wikidata + DBpedia. Two triples with the same (subject, predicate) and
/// different objects (the two sameAs targets) is exactly how RDF represents a multi-valued
/// property -- Doc.writeJsonLd's expanded form renders that as a JSON array for the predicate.
let gameDoc (gameUri: string) : Doc =
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
