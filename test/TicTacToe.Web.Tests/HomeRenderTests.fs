module HomeRenderTests

open Microsoft.AspNetCore.Http
open Expecto
open Oxpecker.ViewEngine
open TicTacToe.Web.templates

// homePage renders purely from (ctx, allowCreate) — no services required.
// The create affordance is the button's data-on:click POST, which is unique to
// the element (the ".new-game-btn" CSS class lives in gameStyles regardless, so
// asserting on the class would false-positive; assert on the POST instead).
let private renderHome (allowCreate: bool) =
    let ctx = DefaultHttpContext()
    Render.toString (home.homePage ctx allowCreate)

let private createAffordance = "@post(&#39;/games&#39;)"

[<Tests>]
let tests =
    testList
        "Home Render Tests"
        [ testCase "New Game button is offered when creation is allowed"
          <| fun _ ->
              let html = renderHome true
              Expect.stringContains html createAffordance "Create affordance present when allowed"

          testCase "New Game button is withheld at the game cap"
          <| fun _ ->
              // ".new-game-btn"/".new-game-container" persist in gameStyles CSS, so the only
              // reliable signal that the button is gone is the absence of its POST affordance.
              let html = renderHome false
              Expect.isFalse (html.Contains createAffordance) "No create affordance when locked" ]
