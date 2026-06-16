module TicTacToe.Engine

/// A game actor that manages a single game instance using MailboxProcessor
/// Implements the actor pattern for handling moves asynchronously
type Game =
    inherit System.IDisposable
    inherit System.IObservable<Model.MoveResult>

    /// Make a move in the game
    abstract MakeMove: Model.Move -> unit

    /// Get current game state synchronously
    abstract GetState: unit -> Model.MoveResult

/// Supervisor that manages game lifecycles and provides game instances
type GameSupervisor =
    inherit System.IDisposable

    /// Create a new supervised game and return its ID and Game reference
    abstract CreateGame: unit -> string * Game

    /// Atomically create a game only if below maxGames; None when at the cap (no TOCTOU)
    abstract TryCreateGame: maxGames: int option -> (string * Game) option

    /// Get an existing game by ID
    abstract GetGame: gameId: string -> Game option

    /// Get count of active games
    abstract GetActiveGameCount: unit -> int

    /// List IDs of all active in-progress games
    abstract ListActiveGames: unit -> string list

    /// Snapshot every active game's id and current state in a single round-trip
    abstract SnapshotActiveGames: unit -> (string * Model.MoveResult) list

    /// Current state of one game from the supervisor's cache; None if it is gone/disposing
    abstract TryGetState: gameId: string -> Model.MoveResult option

/// Create a new game supervisor
val createGameSupervisor: unit -> GameSupervisor
