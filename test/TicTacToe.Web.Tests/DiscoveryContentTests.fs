namespace TicTacToe.Web.Tests

// Idiomatic-ALPS regression guard (raised 2026-07-29): the ALPS profile previously packed every
// field constraint (player enum, the nine position names, rejection rules) into one prose `doc.value`
// string on `make-move` -- informationally complete, but not machine-decomposable the way ALPS is
// meant to be consumed. These tests lock in that `player`/`position` are now their own semantic
// descriptors, referenced by id from the action descriptors via `href`, not just named in prose.

open System.Text.Json
open NUnit.Framework
open TicTacToe.Web

[<TestFixture>]
type DiscoveryContentTests() =

    let alps () =
        JsonDocument.Parse(Discovery.alpsProfile).RootElement.GetProperty("alps")

    let descriptorsOf (alps: JsonElement) =
        alps.GetProperty("descriptor").EnumerateArray() |> List.ofSeq

    let byId (id: string) (descriptors: JsonElement list) =
        descriptors |> List.tryFind (fun d -> d.GetProperty("id").GetString() = id)

    let hrefsOf (d: JsonElement) =
        match d.TryGetProperty("descriptor") with
        | true, nested -> nested.EnumerateArray() |> Seq.map (fun n -> n.GetProperty("href").GetString()) |> List.ofSeq
        | false, _ -> []

    [<Test>]
    member _.``alpsProfile is valid JSON with a top-level alps.version``() =
        let a = alps ()
        Assert.That(a.GetProperty("version").GetString(), Is.EqualTo "1.0")

    [<Test>]
    member _.``player and position are their own semantic descriptors, not just prose``() =
        let ds = alps () |> descriptorsOf
        match ds |> byId "player" with
        | Some d -> Assert.That(d.GetProperty("type").GetString(), Is.EqualTo "semantic")
        | None -> Assert.Fail "expected a top-level 'player' descriptor"
        match ds |> byId "position" with
        | Some d -> Assert.That(d.GetProperty("type").GetString(), Is.EqualTo "semantic")
        | None -> Assert.Fail "expected a top-level 'position' descriptor"

    [<Test>]
    member _.``position enumerates all nine named squares as nested descriptors``() =
        let ds = alps () |> descriptorsOf
        let position = ds |> byId "position" |> Option.get
        let names = position.GetProperty("descriptor").EnumerateArray() |> Seq.map (fun d -> d.GetProperty("id").GetString()) |> List.ofSeq
        let expected =
            [ "TopLeft"; "TopCenter"; "TopRight"
              "MiddleLeft"; "MiddleCenter"; "MiddleRight"
              "BottomLeft"; "BottomCenter"; "BottomRight" ]
        Assert.That(names, Is.EquivalentTo expected)

    [<Test>]
    member _.``make-move references player and position by href, not just by naming them in doc text``() =
        let ds = alps () |> descriptorsOf
        let makeMove = ds |> byId "make-move" |> Option.get
        Assert.That(hrefsOf makeMove, Is.EquivalentTo [ "#player"; "#position" ])

    [<Test>]
    member _.``take-seat is the same wire action as make-move and says so``() =
        let ds = alps () |> descriptorsOf
        let takeSeat = ds |> byId "take-seat" |> Option.get
        Assert.That(hrefsOf takeSeat, Is.EquivalentTo [ "#player"; "#position" ])
        Assert.That(takeSeat.GetProperty("doc").GetProperty("value").GetString(), Does.Contain "make-move",
            "take-seat's doc must state its relationship to make-move -- same POST, different game state")

    [<Test>]
    member _.``game composes board and turn by href instead of one opaque doc string``() =
        let ds = alps () |> descriptorsOf
        let game = ds |> byId "game" |> Option.get
        Assert.That(hrefsOf game, Is.EquivalentTo [ "#board"; "#turn" ])

    [<Test>]
    member _.``the removed /arenas alias is not mentioned anywhere in the profile``() =
        Assert.That(Discovery.alpsProfile, Does.Not.Contain "/arenas", "the /arenas alias route was removed; the profile must not describe it")
