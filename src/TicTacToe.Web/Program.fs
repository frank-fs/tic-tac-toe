open System
open System.IO.Compression
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.ResponseCompression
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open Frank.Builder
open Frank.Auth
open Frank.Datastar
open Frank.OpenApi
open Frank.JsonHome
open Frank.Alps
open TicTacToe.Web
open TicTacToe.Web.Model
open TicTacToe.Web.EventLog
open TicTacToe.Web.Mcp
open TicTacToe.Web.Surface
open TicTacToe.Engine
open TicTacToe.Web.Extensions

/// MCP endpoints are opt-in at startup (this app's default identity is the Frank/Datastar demo;
/// MCP is a research addition the harness turns on explicitly, never a production default).
let private mcpEnabled () =
    match Environment.GetEnvironmentVariable("TICTACTOE_MCP_ENABLED") with
    | "1" | "true" -> true
    | _ -> false

/// Frank.OpenApi's `useOpenApi` mounts the OpenAPI document and Scalar UI unconditionally, on every
/// surface -- verified live under 7.2.0: even at TICTACTOE_CELL=0000 both /openapi/v1.json and
/// /scalar/v1 returned 200, unauthenticated. 7.3.2 moved the document to /.well-known/openapi.json
/// (src/Frank.OpenApi/WebHostBuilderExtensions.fs:16 in the Frank repo); Scalar's own UI route is
/// unaffected. That's a discovery-surface leak an HTTP-arm test run needs to be able to shut off;
/// default stays on (matches current behavior for a human using the app), TICTACTOE_OPENAPI_ENABLED=0
/// turns it off for a run.
let private openApiEnabled () =
    match Environment.GetEnvironmentVariable("TICTACTOE_OPENAPI_ENABLED") with
    | "0" | "false" -> false
    | _ -> true

/// Strips Link-header rel values that Frank 7.3.2's useX operations register as unconditional,
/// app-wide contributions with no per-request hook of their own -- one rel per flag that's
/// supposed to gate that discovery signal: "service-desc" (useOpenApi) when
/// TICTACTOE_OPENAPI_ENABLED is off OR Sd is off (Sd=0 hides ALL discovery signals regardless of
/// other independent toggles -- a floor cell must not leak service-desc just because the OpenAPI
/// toggle happens to default on); "home" (useJsonHome) and "profile" (useAlps) when Sd is off.
/// See the doc comment on useOpenApiGuard/Step 4 above for why this must be an IStartupFilter and
/// not a plugBeforeRouting middleware.
type private DiscoveryLinkScrubStartupFilter(surface: Surface) =
    interface IStartupFilter with
        member _.Configure(next: Action<IApplicationBuilder>) =
            Action<IApplicationBuilder>(fun app ->
                let markers =
                    [ if not (openApiEnabled ()) || not surface.Sd then yield "rel=\"service-desc\""
                      if not surface.Sd then
                          yield "rel=\"profile\""
                          yield "rel=\"home\"" ]

                if not (List.isEmpty markers) then
                    app.Use(fun (ctx: HttpContext) (next2: RequestDelegate) ->
                        task {
                            ctx.Response.OnStarting(fun () ->
                                match ctx.Response.Headers.TryGetValue("Link") with
                                | true, values ->
                                    let kept =
                                        values
                                        |> Seq.filter (fun v -> not (markers |> List.exists v.Contains))
                                        |> Array.ofSeq
                                    if kept.Length = 0 then
                                        ctx.Response.Headers.Remove("Link") |> ignore
                                    else
                                        ctx.Response.Headers.["Link"] <- Microsoft.Extensions.Primitives.StringValues kept
                                | false, _ -> ()
                                Task.CompletedTask)
                            |> ignore
                            do! next2.Invoke ctx
                        }
                        :> Task)
                    |> ignore
                next.Invoke app)

let private initialGames () =
    match Environment.GetEnvironmentVariable("TICTACTOE_INITIAL_GAMES") with
    | null | "" -> 6
    | s ->
        match Int32.TryParse(s) with
        | true, n when n > 0 -> n
        | _ -> 6

let private maxGames () =
    match Environment.GetEnvironmentVariable("TICTACTOE_MAX_GAMES") with
    | null | "" -> None
    | s ->
        match Int32.TryParse(s) with
        | true, n when n > 0 -> Some n
        | _ -> None

let private gameLimits () : GameLimits =
    { InitialGames = initialGames (); MaxGames = maxGames () }

let configureLogging (builder: ILoggingBuilder) =
    builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
    builder.AddFilter("TicTacToe.Web.Auth", LogLevel.Information) |> ignore
    builder

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddHttpContextAccessor() |> ignore

    services
        .AddSingleton<Surface>(Surface.fromEnvironment ())
        .AddSingleton<IStartupFilter>(fun sp ->
            DiscoveryLinkScrubStartupFilter(sp.GetRequiredService<Surface>()) :> IStartupFilter)
        .AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor ())
        .AddSingleton<GameLimits>(fun _ -> gameLimits ())
        .AddSingleton<PlayerAssignmentManager>(fun _ -> PlayerAssignmentManager())
        .AddSingleton<EventLog>(fun _ ->
            match Environment.GetEnvironmentVariable("TICTACTOE_REQUEST_LOG_PATH") with
            | null | "" -> EventLog()
            | path -> EventLog(path))
        .AddSingleton<IClaimsTransformation, GameUserClaimsTransformation>()
        .AddSingleton<Mcp.ToolState>(fun _ -> Mcp.ToolState())
        .AddResponseCompression(fun opts ->
            opts.EnableForHttps <- true

            opts.MimeTypes <-
                ResponseCompressionDefaults.MimeTypes
                |> Seq.append [ "image/svg+xml"; "text/event-stream" ]

            opts.Providers.Add<BrotliCompressionProvider>()
            opts.Providers.Add<GzipCompressionProvider>())
    |> ignore

    services.Configure<BrotliCompressionProviderOptions>(fun (opts: BrotliCompressionProviderOptions) ->
        opts.Level <- CompressionLevel.Fastest)
    |> ignore

    services.Configure<GzipCompressionProviderOptions>(fun (opts: GzipCompressionProviderOptions) ->
        opts.Level <- CompressionLevel.SmallestSize)
    |> ignore

    // Registered unconditionally (cheap DI wiring); whether the endpoints are actually MOUNTED
    // is the mcpEnabled() gate on the `plug` below — a single opt-in switch, not a build-time split.
    services.AddMcpServer().WithHttpTransport().WithTools<Mcp.TicTacToeTools>() |> ignore

    services

/// So: the game URI names the Game (the thing); its RDF description is a distinct document
/// (httpRange-14). `useAlps` (below) contributes rel="profile" app-wide independently -- both
/// coexist as separate Link entries on gameById's GET when Sd+So.
let private describedByLink : HttpContext -> WebLink seq =
    fun ctx ->
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.So then Seq.empty
        else
            let gameId = ctx.Request.RouteValues.["id"] |> string
            Seq.singleton { Target = $"{Handlers.routePrefix ctx}/{gameId}/type"; Rel = "describedby"; Params = [] }

/// Sd: rel="self" on the resource that served the response -- the old hand-rolled
/// discoveryLinks (retired during the Frank.JsonHome/Alps migration) emitted this alongside
/// profile/home; Frank's own useJsonHome/useAlps app-wide providers have no equivalent, so it
/// is restored here per-resource (design spec 2026-06-25-sp2, line 93: `self` is an Sd=1 signal).
let private selfLink : HttpContext -> WebLink seq =
    fun ctx ->
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.Sd then Seq.empty
        else Seq.singleton { Target = string ctx.Request.Path; Rel = "self"; Params = [] }

// Resources
let login =
    resource "/login" {
        name "Login"
        get Handlers.login
    }

let logout =
    resource "/logout" {
        name "Logout"
        get Handlers.logout
    }

let debug =
    resource "/debug" {
        name "Debug"
        get Handlers.debug
    }

let home =
    resource "/" {
        name "Home"
        rel "tag:tictactoe,2026:home"
        requireAuth
        link selfLink
        get Handlers.home
    }

let sse =
    resource "/sse" {
        name "SSE"
        datastar Handlers.sse
    }

let games =
    resource "/games" {
        name "Games"
        rel "tag:tictactoe,2026:games"
        requireAuth
        post (handler { handle Handlers.createGame; binds Discovery.createGame })
    }

let gameById =
    resource "/games/{id}" {
        name "GameById"
        rel "tag:tictactoe,2026:game"
        hrefVar "id" "tag:tictactoe,2026:param;id"
        requireAuth
        link describedByLink
        link selfLink
        get (
            negotiate {
                // "application/ld+json" and "*/*" get an explicit RequestDelegate(...) wrapper, not a
                // bare function reference: this is the documented-safe form (frank-fs/frank#492, see
                // Frank.Rdf.Sample's own getGame) — it names the handler's self-writing intent instead
                // of relying on which NegotiateBuilder overload F#'s resolver happens to pick.
                // "text/html" instead wraps in `handler { }` (a HandlerDefinition, not ambiguous with
                // any FSharpFunc overload either) so it can carry `binds Discovery.viewGame` — Sd's ALPS
                // profile needs a `safe` transition for this GET the same way JSON Home already
                // advertises it as GET-allowed on tag:tictactoe,2026:game.
                //
                // "text/html" listed before "application/ld+json", not a single "*/*": MediaTypeNegotiation
                // ties are broken by registration order (MediaTypeNegotiation.fs), and a registered "*/*"
                // ties at quality 1.0 against ANY single concrete Accept entry -- including a bare
                // "Accept: application/ld+json" with no other entries, and an absent Accept (normalized
                // internally to "*/*"). If "*/*" were the ONLY HTML entry, whichever order wins one of
                // {absent Accept -> HTML default} or {Accept: application/ld+json alone -> ld+json 303}
                // would necessarily lose the other tie. "text/html" (this handler's actual content type)
                // is disjoint from "application/ld+json" -- no tie on an explicit ld+json Accept -- while
                // still winning the tie against ld+json on an absent or explicit "*/*" Accept because it
                // is listed first.
                //
                // The trailing "*/*" entry restores the pre-upgrade "HTML otherwise" fallback for any
                // OTHER concrete Accept (e.g. a bare "Accept: application/json") that matches neither
                // "text/html" nor "application/ld+json" and would otherwise 406. It's listed LAST
                // specifically so it never wins a tie against the two concrete entries above -- it only
                // ever fires when neither of them matches at all.
                accepts "text/html" (handler { handle Handlers.getGameHtml; binds Discovery.viewGame })
                accepts "application/ld+json" (RequestDelegate(Handlers.getGameLdJson))
                accepts "*/*" (RequestDelegate(Handlers.getGameHtml))
            }
        )
        // take-seat and make-move are the same wire action (Discovery.takeSeat's own doc text says
        // so) -- both bind cleanly to this one POST handler. HandlerDefinition.Metadata is a list
        // (binds appends, doesn't replace) and EndpointSurface reads it via
        // GetOrderedMetadata<Descriptor>(), so both descriptors are validated and served.
        post (handler { handle Handlers.makeMove; binds Discovery.makeMove; binds Discovery.takeSeat })
        delete (handler { handle Handlers.deleteGame; binds Discovery.delete })
    }

let gameReset =
    resource "/games/{id}/reset" {
        name "GameReset"
        rel "tag:tictactoe,2026:game-reset"
        hrefVar "id" "tag:tictactoe,2026:param;id"
        requireAuth
        post (handler { handle Handlers.resetGame; binds Discovery.reset })
    }

// No-JS delete: HTML forms cannot emit DELETE, so a POST alias drives the same handler.
// gameDelete stays unbound: Discovery.delete is `idempotent` (valid HTTP methods PUT/DELETE per
// AlpsDocument.validate), and this alias is a POST -- binding it here would fail host startup.
// The real binding lives on gameById's DELETE above.
let gameDelete =
    resource "/games/{id}/delete" {
        name "GameDelete"
        requireAuth
        post Handlers.deleteGame
    }

let gameSse =
    resource "/games/{id}/sse" {
        name "GameSse"
        datastar Handlers.gameSse
    }

// So: the describing document the game URI dereferences to under ld+json conneg.
let gameType =
    resource "/games/{id}/type" {
        name "GameType"
        requireAuth
        get Handlers.gameType
    }

let private optionsAllow (path: string) =
    match path with
    | "/" -> Some "GET, OPTIONS"
    | "/games" -> Some "POST, OPTIONS"
    | "/.well-known/alps.json" | "/.well-known/home.json" -> Some "GET, OPTIONS"
    | p when p.StartsWith "/games/" -> Some "GET, POST, DELETE, OPTIONS"
    | _ -> None

/// Cross-cutting request log: every response's method/path/status_code. Restored after the
/// Surface-twin retirement (9644722) silently dropped the app's only source of this — the twin's
/// own Logger.fs died with it, and nobody re-checked the harness against the merged app's log.
/// Wraps the ENTIRE downstream pipeline (registered first) so it sees the true final status,
/// including auth/404 short-circuits a domain handler never runs for.
let useRequestLog (app: IApplicationBuilder) =
    app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
        task {
            do! next.Invoke ctx
            let eventLog = ctx.RequestServices.GetRequiredService<EventLog.EventLog>()
            match ctx.Items.TryGetValue("rejectionReason") with
            | true, (:? string as r) ->
                eventLog.LogRequest(ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode, reason = r)
            | _ ->
                eventLog.LogRequest(ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode)
        }
        :> Task)

/// Sd: OPTIONS answers "what can I do here?" without a state change. Off entirely when Sd is off.
let useOptionsDiscovery (app: IApplicationBuilder) =
    let surface = app.ApplicationServices.GetRequiredService<Surface>()
    if not surface.Sd then app
    else
        app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
            task {
                match ctx.Request.Method, optionsAllow ctx.Request.Path.Value with
                | "OPTIONS", Some allow ->
                    ctx.Response.StatusCode <- 204
                    ctx.Response.Headers.Append("Allow", allow)
                | _ -> do! next.Invoke ctx
            }
            :> Task)

/// Blocks the OpenAPI document and Scalar UI when openApiEnabled() says no. Frank.OpenApi's
/// `useOpenApi` CE operation has no on/off switch of its own (services + endpoint mounting happen
/// unconditionally), so this intercepts the two routes it mounts before routing ever sees them --
/// same "present in the routing table, 404 unless the toggle is on" shape as useOptionsDiscovery.
let useOpenApiGuard (app: IApplicationBuilder) =
    app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
        task {
            let path = ctx.Request.Path.Value
            let isOpenApiPath =
                path = "/.well-known/openapi.json"
                || path.StartsWith("/scalar", StringComparison.Ordinal)

            if not (openApiEnabled ()) && isOpenApiPath then
                ctx.Response.StatusCode <- 404
            else
                do! next.Invoke ctx
        }
        :> Task)

/// Blocks the JSON Home document when Sd is off. Frank.JsonHome's `useJsonHome` operation has no
/// on/off switch of its own (mounts unconditionally, same as useOpenApi) -- same "present in the
/// routing table, 404 unless the toggle is on" shape as useOpenApiGuard/useOptionsDiscovery.
let useJsonHomeGuard (app: IApplicationBuilder) =
    let surface = app.ApplicationServices.GetRequiredService<Surface>()
    if surface.Sd then app
    else
        app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
            task {
                if ctx.Request.Path.Value = "/.well-known/home.json" then
                    ctx.Response.StatusCode <- 404
                else
                    do! next.Invoke ctx
            }
            :> Task)

/// Blocks the ALPS document when Sd is off. Frank.Alps's `useAlps` operation has no on/off switch
/// of its own (mounts unconditionally, same as useOpenApi/useJsonHome) -- same "present in the
/// routing table, 404 unless the toggle is on" shape as useOpenApiGuard/useJsonHomeGuard.
let useAlpsGuard (app: IApplicationBuilder) =
    let surface = app.ApplicationServices.GetRequiredService<Surface>()
    if surface.Sd then app
    else
        app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
            task {
                if ctx.Request.Path.Value = "/.well-known/alps.json" then
                    ctx.Response.StatusCode <- 404
                else
                    do! next.Invoke ctx
            }
            :> Task)

/// The resource a stream endpoint streams ABOUT — None when the path is not a stream endpoint.
let private streamCanonical (path: string) =
    match path with
    | "/sse" -> Some "/"
    | p when p.EndsWith "/sse" -> Some(p.Substring(0, p.Length - 4))
    | _ -> None

/// Guard the SSE endpoints: a caller that did not ask for text/event-stream gets an immediate 406
/// linking the resource that answers a plain GET, instead of an open stream it would sit on until
/// its own timeout fires. Must run BEFORE routing — the datastar handler flushes stream headers as
/// soon as it is entered, so a status set inside it never reaches the wire.
let useStreamGuard (app: IApplicationBuilder) =
    app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
        task {
            match streamCanonical ctx.Request.Path.Value with
            | Some canonical when not (Handlers.acceptsEventStream ctx) ->
                do! Handlers.rejectNonStream ctx canonical
            | _ -> do! next.Invoke ctx
        }
        :> Task)

/// Create initial games on application startup
let createInitialGames (app: IApplicationBuilder) =
    let lifetime =
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>()

    let supervisor = app.ApplicationServices.GetRequiredService<GameSupervisor>()
    let limits = app.ApplicationServices.GetRequiredService<GameLimits>()
    let surface = app.ApplicationServices.GetRequiredService<Surface>()

    let assignmentManager =
        app.ApplicationServices.GetRequiredService<PlayerAssignmentManager>()

    lifetime.ApplicationStarted.Register(fun () ->
        for _ in 1..limits.InitialGames do
            let (gameId, game) = supervisor.CreateGame()
            Handlers.subscribeToGame surface gameId game assignmentManager supervisor)
    |> ignore

    app

[<EntryPoint>]
let main args =
    webHost args {
        useDefaults

        service configureServices

        logging configureLogging

        plugBeforeRouting useRequestLog

        plugBeforeRoutingWhen isDevelopment DeveloperExceptionPageExtensions.UseDeveloperExceptionPage

        plugBeforeRoutingWhenNot isDevelopment (fun app -> ExceptionHandlerExtensions.UseExceptionHandler(app, "/error", true))

        useAuthentication (fun auth -> auth.AddCookie(fun options -> options.Cookie.Name <- "TicTacToe.User"; options.Cookie.HttpOnly <- true; options.Cookie.SameSite <- SameSiteMode.Strict; options.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest; options.ExpireTimeSpan <- TimeSpan.FromDays(30.0); options.SlidingExpiration <- true; options.LoginPath <- "/login"))

        useAuthorization

        useOpenApi
        useJsonHome
        useAlps Discovery.alpsProfile

        plugBeforeRouting ResponseCompressionBuilderExtensions.UseResponseCompression
        plugBeforeRouting StaticFileExtensions.UseStaticFiles
        // No antiforgery middleware: it only auto-validates endpoints carrying antiforgery
        // metadata (minimal-API form binding), which these manual ctx.Request.Form handlers do
        // not have — so it was a no-op giving a false sense of protection. CSRF on the no-JS
        // state-changing POSTs is mitigated by the SameSite=Strict auth cookie (see useAuthentication),
        // which blocks the cross-site requests an antiforgery token would otherwise guard.
        plugBeforeRouting createInitialGames
        plugBeforeRouting useOptionsDiscovery
        plugBeforeRouting useOpenApiGuard
        plugBeforeRouting useJsonHomeGuard
        plugBeforeRouting useAlpsGuard
        plugBeforeRouting useStreamGuard

        // MCP mounts alongside Frank's own resources — same DI container, same GameSupervisor,
        // same PlayerAssignmentManager, same EventLog (see Mcp.fs). Opt-in only (mcpEnabled).
        // UseEndpoints (not a direct IEndpointRouteBuilder cast): the referenced Frank build runs its
        // classic-host branch here, where `app` is a plain ApplicationBuilder, not WebApplication —
        // UseEndpoints works on either, verified by runtime cast failure when this was a direct cast.
        // Mounted at "/mcp", NOT root: MapMcp()'s bare-root default collides with Frank's own GET "/"
        // home resource (AmbiguousMatchException — verified live, both endpoints claim GET /).
        plug (fun app ->
            if mcpEnabled () then
                app.UseEndpoints(fun endpoints -> endpoints.MapMcp("/mcp") |> ignore)
            else app)

        resource login
        resource logout
        resource debug
        resource home
        resource sse
        resource games
        resource gameById
        resource gameType
        resource gameReset
        resource gameDelete
        resource gameSse
    }

    0
