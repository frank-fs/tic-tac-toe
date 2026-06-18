module TicTacToe.Orchestrator.Tests.ServerLogTailTests

open System
open System.IO
open NUnit.Framework
open TicTacToe.Orchestrator.Types
open TicTacToe.Orchestrator.ServerLogTail

[<TestFixture>]
type ParseEventTests() =

    [<Test>]
    member _.``game_created event parsed correctly``() =
        let json = """{"event_type":"game_created","game_id":"abc","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (GameCreated("abc", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected GameCreated, got: {ev}")

    [<Test>]
    member _.``player_assigned event parsed correctly``() =
        let json = """{"event_type":"player_assigned","game_id":"g1","session_id":"s1","role":"X","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (PlayerAssigned("g1", "s1", "X", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected PlayerAssigned, got: {ev}")

    [<Test>]
    member _.``move_accepted event parsed correctly``() =
        let json = """{"event_type":"move_accepted","game_id":"g1","session_id":"s1","move":"TopLeft","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (MoveAccepted("g1", "s1", "TopLeft", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected MoveAccepted, got: {ev}")

    [<Test>]
    member _.``game_over event parsed correctly``() =
        let json = """{"event_type":"game_over","game_id":"g1","outcome":"x_wins","move_count":5,"timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (GameOver("g1", "x_wins", 5, _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected GameOver, got: {ev}")

    [<Test>]
    member _.``request log entry with rejection_reason parsed as MoveRejected``() =
        let json = """{"request_id":"r1","session_id":"s1","game_id":"g1","player_role":"X","method":"POST","path":"/arenas/g1","status_code":403,"rejection_reason":"OutOfTurn","timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        match ev with
        | Some (MoveRejected("g1", "s1", "OutOfTurn", _)) -> Assert.Pass()
        | _ -> Assert.Fail($"Expected MoveRejected, got: {ev}")

    [<Test>]
    member _.``request log without rejection_reason returns None``() =
        let json = """{"request_id":"r1","session_id":"s1","game_id":"g1","method":"GET","path":"/arenas/g1","status_code":200,"timestamp":"2026-06-11T10:00:00Z"}"""
        let ev = parseLogLine json
        Assert.That(ev, Is.EqualTo(None))

    [<Test>]
    member _.``malformed JSON returns None``() =
        let ev = parseLogLine "{not valid json"
        Assert.That(ev, Is.EqualTo(None))

[<TestFixture>]
type TailTests() =

    [<Test>]
    member _.``getEvents returns all parsed events from a file``() =
        let path = Path.GetTempFileName()
        File.WriteAllLines(path, [|
            """{"event_type":"game_created","game_id":"g1","timestamp":"2026-06-11T10:00:00Z"}"""
            """{"event_type":"player_assigned","game_id":"g1","session_id":"s1","role":"X","timestamp":"2026-06-11T10:00:01Z"}"""
        |])
        let tail = startTail path
        let events = tail.GetEvents()
        Assert.That(events.Length, Is.EqualTo(2))
        File.Delete(path)
