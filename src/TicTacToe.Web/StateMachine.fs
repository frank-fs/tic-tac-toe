module TicTacToe.Web.GameStateMachine

open Frank.Statecharts
open Frank.Resources.Model
open TicTacToe.Web.Model

// ============================================================================
// Transition function
// ============================================================================

/// Statechart context: unit (all real game state lives in Engine's GameSupervisor).
/// The statechart only tracks phase transitions for routing/affordances.
let gameTransition (state: GamePhase) (event: GameEvent) (_ctx: unit) =
    match state, event with
    // A move from XTurn can go to OTurn, Won, Draw, or Error
    // The actual outcome is determined by the Engine; the statechart store
    // is updated by the observer AFTER the Engine processes the move.
    // For the statechart middleware transition, we just allow the transition.
    | GamePhase.XTurn, GameEvent.MakeMove _ -> TransitionResult.Transitioned(GamePhase.OTurn, ())
    | GamePhase.OTurn, GameEvent.MakeMove _ -> TransitionResult.Transitioned(GamePhase.XTurn, ())
    // Reset creates a new game — the old game's statechart is not reused
    | _, GameEvent.Reset -> TransitionResult.Transitioned(GamePhase.XTurn, ())
    // Delete is a terminal action
    | _, GameEvent.Delete -> TransitionResult.Invalid "Delete removes the game"
    // Cannot move in terminal states
    | GamePhase.Won, GameEvent.MakeMove _ -> TransitionResult.Invalid "Game already over"
    | GamePhase.Draw, GameEvent.MakeMove _ -> TransitionResult.Invalid "Game already over"
    | GamePhase.Error, GameEvent.MakeMove _ -> TransitionResult.Invalid "Game in error state"

// ============================================================================
// Guards
// ============================================================================

/// Turn guard: checks that the user has the correct player claim for the current turn.
/// The role predicates resolve "PlayerX"/"PlayerO" from claims; this guard
/// checks that the resolved role matches the current phase.
let turnGuard: Guard<GamePhase, GameEvent, unit> =
    AccessControl(
        "TurnGuard",
        fun ctx ->
            match ctx.CurrentState with
            | GamePhase.XTurn ->
                if ctx.HasRole("PlayerX") then GuardResult.Allowed
                elif ctx.HasRole("PlayerO") then GuardResult.Blocked BlockReason.NotYourTurn
                else
                    // Unassigned user — allow (assignment happens in handler)
                    GuardResult.Allowed
            | GamePhase.OTurn ->
                if ctx.HasRole("PlayerO") then GuardResult.Allowed
                elif ctx.HasRole("PlayerX") then GuardResult.Blocked BlockReason.NotYourTurn
                else
                    // Unassigned user — allow (assignment happens in handler)
                    GuardResult.Allowed
            | GamePhase.Won | GamePhase.Draw | GamePhase.Error ->
                // GET is allowed in terminal states; POST will be blocked by method check
                GuardResult.Allowed
    )

// ============================================================================
// State machine definition
// ============================================================================

let gameMachine: StateMachine<GamePhase, GameEvent, unit> =
    { Initial = GamePhase.XTurn
      InitialContext = ()
      Transition = gameTransition
      Guards = [ turnGuard ]
      StateMetadata =
        Map.ofList
            [ GamePhase.XTurn,
              { AllowedMethods = [ "GET"; "POST"; "DELETE" ]
                IsFinal = false
                Description = Some "X's turn to play" }
              GamePhase.OTurn,
              { AllowedMethods = [ "GET"; "POST"; "DELETE" ]
                IsFinal = false
                Description = Some "O's turn to play" }
              GamePhase.Won,
              { AllowedMethods = [ "GET"; "DELETE" ]
                IsFinal = true
                Description = Some "Game over: someone won" }
              GamePhase.Draw,
              { AllowedMethods = [ "GET"; "DELETE" ]
                IsFinal = true
                Description = Some "Game over: draw" }
              GamePhase.Error,
              { AllowedMethods = [ "GET" ]
                IsFinal = true
                Description = Some "Game in error state" } ] }
