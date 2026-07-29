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
        | true, nested ->
            nested.EnumerateArray()
            |> Seq.choose (fun n -> match n.TryGetProperty("href") with | true, v -> Some(v.GetString()) | false, _ -> None)
            |> List.ofSeq
        | false, _ -> []

    let nestedIdsOf (d: JsonElement) =
        match d.TryGetProperty("descriptor") with
        | true, nested ->
            nested.EnumerateArray()
            |> Seq.choose (fun n -> match n.TryGetProperty("id") with | true, v -> Some(v.GetString()) | false, _ -> None)
            |> List.ofSeq
        | false, _ -> []

    let rec allIdsOf (d: JsonElement) : string list =
        let ownId = match d.TryGetProperty("id") with | true, v -> [ v.GetString() ] | false, _ -> []
        let childIds =
            match d.TryGetProperty("descriptor") with
            | true, nested -> nested.EnumerateArray() |> Seq.collect allIdsOf |> List.ofSeq
            | false, _ -> []
        ownId @ childIds

    let rec allHrefsOf (d: JsonElement) : string list =
        let ownHref = match d.TryGetProperty("href") with | true, v -> [ v.GetString() ] | false, _ -> []
        let childHrefs =
            match d.TryGetProperty("descriptor") with
            | true, nested -> nested.EnumerateArray() |> Seq.collect allHrefsOf |> List.ofSeq
            | false, _ -> []
        ownHref @ childHrefs

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

    [<Test>]
    member _.``player enumerates X and O as nested descriptors, not just prose``() =
        let ds = alps () |> descriptorsOf
        let player = ds |> byId "player" |> Option.get
        Assert.That(nestedIdsOf player, Is.EquivalentTo [ "X"; "O" ])

    [<Test>]
    member _.``square-state references player for the Taken case and names the Empty case``() =
        let ds = alps () |> descriptorsOf
        let squareState = ds |> byId "square-state" |> Option.get
        Assert.That(hrefsOf squareState, Is.EquivalentTo [ "#player" ])
        Assert.That(nestedIdsOf squareState, Is.EquivalentTo [ "empty" ])

    [<Test>]
    member _.``turn composes player (whose move) and outcome (terminal result) by href``() =
        let ds = alps () |> descriptorsOf
        Assert.That(hrefsOf (ds |> byId "turn" |> Option.get), Is.EquivalentTo [ "#player"; "#outcome" ])
        let outcome = ds |> byId "outcome" |> Option.get
        Assert.That(hrefsOf outcome, Is.EquivalentTo [ "#player" ], "outcome must reference player by href to name the winner, not prose")
        Assert.That(nestedIdsOf outcome, Is.EquivalentTo [ "draw" ])

    [<Test>]
    member _.``reset and delete take no body, so reference no fields``() =
        let ds = alps () |> descriptorsOf
        Assert.That(hrefsOf (ds |> byId "reset" |> Option.get), Is.Empty)
        Assert.That(hrefsOf (ds |> byId "delete" |> Option.get), Is.Empty)

    [<Test>]
    member _.``every href in the profile resolves to a real descriptor id somewhere in the document``() =
        let ds = alps () |> descriptorsOf
        let allIds = ds |> List.collect allIdsOf |> Set.ofList
        let allHrefs = ds |> List.collect allHrefsOf
        for href in allHrefs do
            let target = href.TrimStart('#')
            Assert.That(allIds.Contains target, Is.True, sprintf "dangling href '%s' does not resolve to any descriptor id" href)
