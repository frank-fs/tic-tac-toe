namespace TicTacToe.Web.Tests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open TicTacToe.Web.SseBroadcast

/// Pure unit tests for per-game SSE routing. No server/browser: subscribe channels
/// directly, broadcast, then assert which channels received an event.
[<TestFixture>]
type SseBroadcastTests() =

    // A render that ignores the writer; we only care about delivery, not payload.
    let noopRender : TextWriter -> Task = fun _ -> Task.CompletedTask

    /// True if the channel has at least one queued event (non-blocking).
    let received (ch: System.Threading.Channels.Channel<SseEvent>) =
        ch.Reader.Count > 0

    [<Test>]
    member _.``per-game broadcast reaches dashboard and that game, not other games``() =
        let dash, dashSub = subscribe "u-dash" None
        let g1, g1Sub = subscribe "u-g1" (Some "game-1")
        let g2, g2Sub = subscribe "u-g2" (Some "game-2")
        try
            broadcastPerRoleForGame "game-1" (fun _ -> PatchElements noopRender)
            Assert.That(received dash, Is.True, "dashboard (None filter) must receive every game's events")
            Assert.That(received g1, Is.True, "the game-1 subscriber must receive game-1 events")
            Assert.That(received g2, Is.False, "the game-2 subscriber must NOT receive game-1 events")
        finally
            dashSub.Dispose(); g1Sub.Dispose(); g2Sub.Dispose()

    [<Test>]
    member _.``dashboard broadcast reaches only dashboard subscribers``() =
        let dash, dashSub = subscribe "u-dash" None
        let g1, g1Sub = subscribe "u-g1" (Some "game-1")
        try
            broadcastToDashboard (PatchElementsAppend("#games-container", noopRender))
            Assert.That(received dash, Is.True, "dashboard must receive a new-game append")
            Assert.That(received g1, Is.False, "a per-game subscriber must NOT receive another game's append")
        finally
            dashSub.Dispose(); g1Sub.Dispose()
