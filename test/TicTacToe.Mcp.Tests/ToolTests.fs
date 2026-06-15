module TicTacToe.Mcp.Tests.ToolTests

open System.Text.Json.Nodes
open NUnit.Framework
open TicTacToe.Engine
open TicTacToe.Mcp.Tools

let private makeTools () =
    GameTools(createGameSupervisor(), PlayerRegistry())

let private parseObj (json: string) = JsonNode.Parse(json) :?> JsonObject

let private str (node: JsonNode) = node.GetValue<string>()

/// New game with both players joined; returns (tools, gameId, tokenX, tokenO).
let private newJoinedGame () =
    let tools = makeTools()
    let gameId = str (parseObj (tools.``new_game``())["gameId"])
    let tokenX = str (parseObj (tools.``join_game``(gameId, "X"))["playerToken"])
    let tokenO = str (parseObj (tools.``join_game``(gameId, "O"))["playerToken"])
    tools, gameId, tokenX, tokenO

let private intCount (node: JsonNode) = (node :?> JsonArray).Count

[<TestFixture>]
type NewGameTests() =

    [<Test>]
    member _.``returns gameId, 9-cell board, X turn, in_progress``() =
        let tools = makeTools()
        let obj = parseObj (tools.``new_game``())
        Assert.That(obj.ContainsKey("gameId"), Is.True)
        Assert.That((obj["board"] :?> JsonObject).Count, Is.EqualTo(9))
        Assert.That(str obj["whoseTurn"], Is.EqualTo("X"))
        Assert.That(str obj["status"], Is.EqualTo("in_progress"))

    [<Test>]
    member _.``second call returns MaxGamesReached``() =
        let tools = makeTools()
        tools.``new_game``() |> ignore
        let obj = parseObj (tools.``new_game``())
        Assert.That(str obj["error"], Is.EqualTo("MaxGamesReached"))

[<TestFixture>]
type GetBoardTests() =

    [<Test>]
    member _.``returns board for valid game``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        let obj = parseObj(tools.``get_board``(gameId))
        Assert.That(obj.ContainsKey("board"), Is.True)
        Assert.That(obj.ContainsKey("error"), Is.False)

    [<Test>]
    member _.``returns GameNotFound for unknown id``() =
        let tools = makeTools()
        let obj = parseObj(tools.``get_board``("no-such-game"))
        Assert.That(str obj["error"], Is.EqualTo("GameNotFound"))

[<TestFixture>]
type JoinGameTests() =

    [<Test>]
    member _.``join assigns requested role and returns a token``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        let obj = parseObj(tools.``join_game``(gameId, "X"))
        Assert.That(str obj["role"], Is.EqualTo("X"))
        Assert.That(obj.ContainsKey("playerToken"), Is.True)

    [<Test>]
    member _.``third joiner is rejected with GameFull``() =
        let tools, gameId, _, _ = newJoinedGame()
        let obj = parseObj(tools.``join_game``(gameId, ""))
        Assert.That(str obj["error"], Is.EqualTo("GameFull"))

    [<Test>]
    member _.``claiming a taken role returns RoleTaken``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        tools.``join_game``(gameId, "X") |> ignore
        let obj = parseObj(tools.``join_game``(gameId, "X"))
        Assert.That(str obj["error"], Is.EqualTo("RoleTaken"))

[<TestFixture>]
type MakeMoveTests() =

    [<Test>]
    member _.``valid move updates board and alternates turn``() =
        let tools, gameId, tokenX, _ = newJoinedGame()
        let obj = parseObj(tools.``make_move``(gameId, tokenX, "TopLeft"))
        Assert.That(obj.ContainsKey("error"), Is.False)
        Assert.That(str obj["whoseTurn"], Is.EqualTo("O"))

    [<Test>]
    member _.``position already taken returns PositionTaken``() =
        let tools, gameId, tokenX, tokenO = newJoinedGame()
        tools.``make_move``(gameId, tokenX, "TopLeft") |> ignore   // X
        tools.``make_move``(gameId, tokenO, "TopCenter") |> ignore // O
        let obj = parseObj(tools.``make_move``(gameId, tokenX, "TopLeft")) // X, taken
        Assert.That(str obj["error"], Is.EqualTo("PositionTaken"))

    [<Test>]
    member _.``invalid position name returns InvalidPosition``() =
        let tools, gameId, tokenX, _ = newJoinedGame()
        let obj = parseObj(tools.``make_move``(gameId, tokenX, "NotASquare"))
        Assert.That(str obj["error"], Is.EqualTo("InvalidPosition"))

    [<Test>]
    member _.``move on finished game returns GameOver``() =
        let tools, gameId, tokenX, tokenO = newJoinedGame()
        tools.``make_move``(gameId, tokenX, "TopLeft")      |> ignore // X
        tools.``make_move``(gameId, tokenO, "MiddleLeft")   |> ignore // O
        tools.``make_move``(gameId, tokenX, "TopCenter")    |> ignore // X
        tools.``make_move``(gameId, tokenO, "MiddleCenter") |> ignore // O
        tools.``make_move``(gameId, tokenX, "TopRight")     |> ignore // X wins top row
        let obj = parseObj(tools.``make_move``(gameId, tokenO, "BottomLeft"))
        Assert.That(str obj["error"], Is.EqualTo("GameOver"))

    [<Test>]
    member _.``unknown game returns GameNotFound``() =
        let tools = makeTools()
        let obj = parseObj(tools.``make_move``("no-game", "any-token", "TopLeft"))
        Assert.That(str obj["error"], Is.EqualTo("GameNotFound"))

    [<Test>]
    member _.``move with an unknown token returns NotAPlayer``() =
        let tools, gameId, _, _ = newJoinedGame()
        let obj = parseObj(tools.``make_move``(gameId, "bogus-token", "TopLeft"))
        Assert.That(str obj["error"], Is.EqualTo("NotAPlayer"))

    [<Test>]
    member _.``moving on the opponent's turn returns NotYourTurn``() =
        let tools, gameId, tokenX, tokenO = newJoinedGame()
        tools.``make_move``(gameId, tokenX, "TopLeft") |> ignore // X moves, now O's turn
        let obj = parseObj(tools.``make_move``(gameId, tokenX, "TopCenter")) // X again
        Assert.That(str obj["error"], Is.EqualTo("NotYourTurn"))

[<TestFixture>]
type GetStateTests() =

    [<Test>]
    member _.``includes gameId in response``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        let obj = parseObj(tools.``get_state``(gameId))
        Assert.That(str obj["gameId"], Is.EqualTo(gameId))

    [<Test>]
    member _.``unknown game returns GameNotFound``() =
        let tools = makeTools()
        let obj = parseObj(tools.``get_state``("no-game"))
        Assert.That(str obj["error"], Is.EqualTo("GameNotFound"))
