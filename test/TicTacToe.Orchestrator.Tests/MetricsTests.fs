module TicTacToe.Orchestrator.Tests.MetricsTests

open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.Metrics

let private makeHttp outcome =
    Http { Turn = 0; Method = "GET"; Url = "/"; RequestHeaders = Map.empty; RequestBody = None
           StatusCode = 200; ResponseHeaders = Map.empty; ResponseBody = ""
           Outcome = outcome; Strategy = BlindPost }

[<TestFixture>]
type MetricsTests() =

    // AT4: 3 valid + 2 invalid + 1 discovery → RPVA = (3+2+1)/3 = 2.0; invalid_rate = 2/6 = 0.33
    [<Test>]
    member _.``AT4 canned transcript RPVA 2 point 0 invalid rate 0 point 33``() =
        let transcript = [
            makeHttp ValidAction
            makeHttp ValidAction
            makeHttp ValidAction
            makeHttp InvalidAction
            makeHttp InvalidAction
            makeHttp Discovery
        ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Rpva, Is.EqualTo(2.0).Within(0.001))
        Assert.That(metrics.InvalidRate, Is.EqualTo(0.333).Within(0.001))
        Assert.That(metrics.Abandoned, Is.False)

    [<Test>]
    member _.``one discovery plus one valid action gives RPVA 2 point 0``() =
        let transcript = [ makeHttp Discovery; makeHttp ValidAction ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Rpva, Is.EqualTo(2.0).Within(0.001))

    [<Test>]
    member _.``no valid actions gives abandoned true``() =
        let transcript = [ makeHttp Discovery ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Abandoned, Is.True)

    [<Test>]
    member _.``no valid actions gives RPVA MaxValue``() =
        let transcript = [ makeHttp InvalidAction; makeHttp Discovery ]
        let metrics = computeMetrics transcript 0
        Assert.That(metrics.Rpva, Is.EqualTo(System.Double.MaxValue))

    [<Test>]
    member _.``aggregate averages RPVA invalid rate and tokens across games``() =
        let g1 = { Transcript = [makeHttp ValidAction]; Metrics = { Rpva = 2.0; InvalidRate = 0.0; Abandoned = false; Tokens = 100 } }
        let g2 = { Transcript = [makeHttp ValidAction]; Metrics = { Rpva = 4.0; InvalidRate = 0.5; Abandoned = false; Tokens = 200 } }
        let agg = aggregate [g1; g2]
        Assert.That(agg.Rpva, Is.EqualTo(3.0).Within(0.001))
        Assert.That(agg.InvalidRate, Is.EqualTo(0.25).Within(0.001))
        Assert.That(agg.AbandonRate, Is.EqualTo(0.0).Within(0.001))
        Assert.That(agg.Tokens, Is.EqualTo(150.0).Within(0.001))
