module TicTacToe.Mcp.Tests.ToolTests

open System.Text.Json.Nodes
open NUnit.Framework
open TicTacToe.Engine
open TicTacToe.Mcp.Tools

let private makeTools () =
    GameTools(createGameSupervisor())

let private parseObj (json: string) = JsonNode.Parse(json) :?> JsonObject

let private str (node: JsonNode) = node.GetValue<string>()

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
type MakeMoveTests() =

    [<Test>]
    member _.``valid move updates board and alternates turn``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        let obj = parseObj(tools.``make_move``(gameId, "TopLeft"))
        Assert.That(obj.ContainsKey("error"), Is.False)
        Assert.That(str obj["whoseTurn"], Is.EqualTo("O"))

    [<Test>]
    member _.``position already taken returns PositionTaken``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        tools.``make_move``(gameId, "TopLeft") |> ignore
        tools.``make_move``(gameId, "TopCenter") |> ignore
        let obj = parseObj(tools.``make_move``(gameId, "TopLeft"))
        Assert.That(str obj["error"], Is.EqualTo("PositionTaken"))

    [<Test>]
    member _.``invalid position name returns InvalidPosition``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        let obj = parseObj(tools.``make_move``(gameId, "NotASquare"))
        Assert.That(str obj["error"], Is.EqualTo("InvalidPosition"))

    [<Test>]
    member _.``move on finished game returns GameOver``() =
        let tools = makeTools()
        let gameId = str (parseObj (tools.``new_game``())["gameId"])
        tools.``make_move``(gameId, "TopLeft")      |> ignore // X
        tools.``make_move``(gameId, "MiddleLeft")   |> ignore // O
        tools.``make_move``(gameId, "TopCenter")    |> ignore // X
        tools.``make_move``(gameId, "MiddleCenter") |> ignore // O
        tools.``make_move``(gameId, "TopRight")     |> ignore // X wins top row
        let obj = parseObj(tools.``make_move``(gameId, "BottomLeft"))
        Assert.That(str obj["error"], Is.EqualTo("GameOver"))

    [<Test>]
    member _.``unknown game returns GameNotFound``() =
        let tools = makeTools()
        let obj = parseObj(tools.``make_move``("no-game", "TopLeft"))
        Assert.That(str obj["error"], Is.EqualTo("GameNotFound"))

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
