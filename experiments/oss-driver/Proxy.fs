module TicTacToe.OssDriver.Proxy

// Logging reverse-proxy. Forwards verbatim to the real server and appends one JSONL line per
// request ({ts,method,path,status}) — the wire log everything else is graded against. Ported
// from proxy.py. Redirects are NOT followed (302 passes through). Cookies/headers pass through
// unchanged (UseCookies=false), so Set-Cookie and Link/Allow reach the agent exactly as served.
//
//   dotnet run --project experiments/oss-driver -- proxy <listenPort> <targetPort> <logPath>

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading

// Hop-by-hop headers must not be forwarded (RFC 7230 §6.1).
let private hopByHop =
    set [ "connection"; "keep-alive"; "proxy-authenticate"; "proxy-authorization"
          "te"; "trailers"; "transfer-encoding"; "upgrade"; "content-length" ]

let run (argv: string[]) : int =
    if argv.Length < 3 then
        eprintfn "usage: proxy <listenPort> <targetPort> <logPath>"
        2
    else
        let listenPort = int argv.[0]
        let targetPort = int argv.[1]
        let logPath = argv.[2]
        let target = sprintf "http://localhost:%d" targetPort
        File.WriteAllText(logPath, "")  // fresh log per run
        let logLock = obj ()
        let logReq (m: string) (path: string) (status: int) =
            let o = JsonObject()
            o.["ts"] <- JsonValue.Create(float (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0)
            o.["method"] <- JsonValue.Create m
            o.["path"] <- JsonValue.Create path
            o.["status"] <- JsonValue.Create status
            lock logLock (fun () -> File.AppendAllText(logPath, o.ToJsonString() + "\n"))

        use client = new HttpClient(new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false))

        let handle (ctx: HttpListenerContext) =
            let req = ctx.Request
            let resp = ctx.Response
            let m = req.HttpMethod
            let rawUrl = req.RawUrl
            try
                use msg = new HttpRequestMessage(HttpMethod(m), target + rawUrl)
                if req.HasEntityBody then
                    use ms = new MemoryStream()
                    req.InputStream.CopyTo ms
                    msg.Content <- new ByteArrayContent(ms.ToArray())
                for key in req.Headers.AllKeys do
                    let kl = key.ToLowerInvariant()
                    if not (hopByHop.Contains kl) && kl <> "host" then
                        let v = req.Headers.[key]
                        if not (msg.Headers.TryAddWithoutValidation(key, v)) && not (isNull msg.Content) then
                            msg.Content.Headers.TryAddWithoutValidation(key, v) |> ignore
                use up = client.Send msg
                let status = int up.StatusCode
                logReq m rawUrl status
                resp.StatusCode <- status
                let relay (h: Headers.HttpHeaders) =
                    for kv in h do
                        let kl = kv.Key.ToLowerInvariant()
                        if hopByHop.Contains kl then ()
                        elif kl = "content-type" then resp.ContentType <- String.Join(",", kv.Value)
                        else for v in kv.Value do (try resp.Headers.Add(kv.Key, v) with _ -> ())  // Set-Cookie replayed individually
                relay up.Headers
                relay up.Content.Headers
                let data = up.Content.ReadAsByteArrayAsync().Result
                resp.ContentLength64 <- int64 data.Length
                resp.OutputStream.Write(data, 0, data.Length)
            with e ->
                (try logReq m rawUrl 502 with _ -> ())
                (try resp.StatusCode <- 502 with _ -> ())
            (try resp.OutputStream.Close() with _ -> ())

        let listener = new HttpListener()
        listener.Prefixes.Add(sprintf "http://localhost:%d/" listenPort)
        listener.Start()
        eprintfn "proxy: localhost:%d -> localhost:%d (log %s)" listenPort targetPort logPath
        while true do
            let ctx = listener.GetContext()
            ThreadPool.QueueUserWorkItem(fun _ -> handle ctx) |> ignore
        0
