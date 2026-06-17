module TicTacToe.McpRpc.Tests.IdentityTests

open Expecto
open TicTacToe.McpRpc.Identity

[<Tests>]
let sessionIdentityTests =
    testList
        "SessionIdentity"
        [ testCase "starts unauthenticated"
          <| fun _ ->
              let s = SessionIdentity()
              Expect.isNone s.Current "no token before authenticate"

          testCase "Authenticate sets a token and returns it"
          <| fun _ ->
              let s = SessionIdentity()
              let t = s.Authenticate()
              Expect.isNotNull t "token is returned"
              Expect.equal s.Current (Some t) "Current reflects the minted token"

          testCase "Authenticate mints distinct tokens"
          <| fun _ ->
              let a = SessionIdentity().Authenticate()
              let b = SessionIdentity().Authenticate()
              Expect.notEqual a b "tokens are unique" ]

open TicTacToe.Model

[<Tests>]
let assignmentStoreTests =
    testList
        "PlayerAssignmentStore"
        [ testCase "first mover on X's turn is assigned X"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              let r = store.TryAssignAndValidate("g1", "tokA", true)
              Expect.equal r (Allowed X) "tokA binds to X"

          testCase "second distinct token on O's turn is assigned O"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              store.TryAssignAndValidate("g1", "tokA", true) |> ignore
              let r = store.TryAssignAndValidate("g1", "tokB", false)
              Expect.equal r (Allowed O) "tokB binds to O"

          testCase "X player moving on O's turn is rejected NotYourTurn"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              store.TryAssignAndValidate("g1", "tokA", true) |> ignore
              store.TryAssignAndValidate("g1", "tokB", false) |> ignore
              let r = store.TryAssignAndValidate("g1", "tokA", false)
              Expect.equal r (Rejected NotYourTurn) "X cannot move on O's turn"

          testCase "third token in a full game is rejected NotAPlayer"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              store.TryAssignAndValidate("g1", "tokA", true) |> ignore
              store.TryAssignAndValidate("g1", "tokB", false) |> ignore
              let r = store.TryAssignAndValidate("g1", "tokC", true)
              Expect.equal r (Rejected NotAPlayer) "spectator rejected"

          testCase "one token holds independent seats across two games"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              let r1 = store.TryAssignAndValidate("g1", "tokA", true)
              let r2 = store.TryAssignAndValidate("g2", "tokA", true)
              Expect.equal r1 (Allowed X) "X in g1"
              Expect.equal r2 (Allowed X) "X in g2 — independent binding" ]
