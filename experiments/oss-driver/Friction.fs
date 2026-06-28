module TicTacToe.OssDriver.Friction

// Classify per-arm request friction into reads / accepted-writes / rejected-writes /
// auth-redirects, so polling is not conflated with rejection. Ported from friction.py.
//   proxy log: one {ts,method,path,status} per line (the F# proxy subcommand's output)
//   erpc  log: one {event_type,game_id,...} per line
//
//   dotnet run --project experiments/oss-driver -- friction proxy /tmp/arena-surface.http.jsonl
//   dotnet run --project experiments/oss-driver -- friction erpc  /tmp/erpc-game.jsonl [gameId]

open System.IO
open System.Text.Json

let run (argv: string[]) : int =
    if argv.Length < 2 then
        eprintfn "usage: friction <proxy|erpc> <log> [gameId]"
        2
    else
        let kind = argv.[0]
        let path = argv.[1]
        let game = if argv.Length > 2 then Some argv.[2] else None
        let mutable reads, ok, rej, auth, other = 0, 0, 0, 0, 0
        for raw in File.ReadLines path do
            let line = raw.Trim()
            if line <> "" then
                try
                    use doc = JsonDocument.Parse line
                    let root = doc.RootElement
                    let getS (name: string) =
                        match root.TryGetProperty name with
                        | true, v -> v.GetString()
                        | _ -> null
                    let getI (name: string) =
                        match root.TryGetProperty name with
                        | true, v -> (try v.GetInt32() with _ -> 0)
                        | _ -> 0
                    if kind = "proxy" then
                        let m, status = getS "method", getI "status"
                        if status = 302 then auth <- auth + 1
                        elif m = "GET" then reads <- reads + 1
                        elif m = "POST" && (status = 303 || status = 200) then ok <- ok + 1
                        elif m = "POST" && status >= 400 then rej <- rej + 1
                        else other <- other + 1
                    elif kind = "erpc" then
                        let skip = game.IsSome && getS "game_id" <> game.Value
                        if not skip then
                            match getS "event_type" with
                            | "state_read" -> reads <- reads + 1
                            | "move_accepted" -> ok <- ok + 1
                            | "move_rejected" -> rej <- rej + 1
                            | _ -> ()
                with _ -> ()
        let writes = ok + rej
        let ratio = if writes > 0 then sprintf "%.1f:1" (float reads / float writes) else "n/a"
        printfn "%s" (Path.GetFileName path)
        printfn "  reads (poll):     %d" reads
        printfn "  writes accepted:  %d" ok
        printfn "  writes rejected:  %d" rej
        printfn "  auth redirects:   %d" auth
        if other > 0 then printfn "  other:            %d" other
        printfn "  read:write ratio: %s   (rejections are NOT counted as reads)" ratio
        0
