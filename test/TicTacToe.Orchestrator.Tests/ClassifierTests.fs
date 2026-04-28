module TicTacToe.Orchestrator.Tests.ClassifierTests

open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Classifier

[<TestFixture>]
type OutcomeTests() =

    [<Test>]
    member _.``200 POST to arena path is ValidAction``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123" 200 []
        Assert.That(outcome, Is.EqualTo(ValidAction))

    [<Test>]
    member _.``200 GET is Discovery``() =
        let outcome = classifyOutcome "GET" "/" 200 []
        Assert.That(outcome, Is.EqualTo(Discovery))

    [<Test>]
    member _.``200 GET arena is Discovery not ValidAction``() =
        let outcome = classifyOutcome "GET" "/arenas/abc123" 200 []
        Assert.That(outcome, Is.EqualTo(Discovery))

    [<Test>]
    member _.``422 response is InvalidAction``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123" 422 []
        Assert.That(outcome, Is.EqualTo(InvalidAction))

    [<Test>]
    member _.``400 response is InvalidAction``() =
        let outcome = classifyOutcome "POST" "/arenas/abc123" 400 []
        Assert.That(outcome, Is.EqualTo(InvalidAction))

    [<Test>]
    member _.``repeated method+url is Retry``() =
        let prior = [("POST", "/arenas/abc123")]
        let outcome = classifyOutcome "POST" "/arenas/abc123" 200 prior
        Assert.That(outcome, Is.EqualTo(Retry))

[<TestFixture>]
type StrategyTests() =

    [<Test>]
    member _.``URL found in prior response body is HtmlFollow``() =
        let priorBodies = ["/arenas/abc123"]
        let strategy = classifyStrategy "POST" "/arenas/abc123" priorBodies false
        Assert.That(strategy, Is.EqualTo(HtmlFollow))

    [<Test>]
    member _.``URL found in OpenAPI doc is SpecFollow``() =
        let strategy = classifyStrategy "GET" "/arenas/abc123" [] true
        Assert.That(strategy, Is.EqualTo(SpecFollow))

    [<Test>]
    member _.``URL not in any prior response is BlindPost``() =
        let strategy = classifyStrategy "POST" "/arenas/xyz999" [] false
        Assert.That(strategy, Is.EqualTo(BlindPost))

    [<Test>]
    member _.``URL in prior body takes precedence over spec``() =
        let strategy = classifyStrategy "POST" "/arenas/abc123" ["/arenas/abc123"] true
        Assert.That(strategy, Is.EqualTo(HtmlFollow))
