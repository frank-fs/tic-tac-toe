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
