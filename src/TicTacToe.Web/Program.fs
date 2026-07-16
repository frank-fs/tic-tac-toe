open System
open System.IO.Compression
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
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
        requireAuth
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
        requireAuth
        post Handlers.createGame
    }

let gameById =
    resource "/games/{id}" {
        name "GameById"
        requireAuth
        get Handlers.getGame
        post Handlers.makeMove
        delete Handlers.deleteGame
    }

let gameReset =
    resource "/games/{id}/reset" {
        name "GameReset"
        requireAuth
        post Handlers.resetGame
    }

// No-JS delete: HTML forms cannot emit DELETE, so a POST alias drives the same handler.
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

// /arenas is an ALIAS of /games: the SAME handlers, the same representations. It exists so the
// banked experiment surface (which every archived result was produced against) is reachable under
// the name it was produced under; /games is the product name for the identical resource.
let arenas =
    resource "/arenas" {
        name "Arenas"
        requireAuth
        post Handlers.createGame
    }

let arenaById =
    resource "/arenas/{id}" {
        name "ArenaById"
        requireAuth
        get Handlers.getGame
        post Handlers.makeMove
        delete Handlers.deleteGame
    }

let arenaType =
    resource "/arenas/{id}/type" {
        name "ArenaType"
        requireAuth
        get Handlers.gameType
    }

// The twin called it `restart`; the product calls it `reset`. Both names, one handler.
let arenaRestart =
    resource "/arenas/{id}/restart" {
        name "ArenaRestart"
        requireAuth
        post Handlers.resetGame
    }

let arenaReset =
    resource "/arenas/{id}/reset" {
        name "ArenaReset"
        requireAuth
        post Handlers.resetGame
    }

let arenaDelete =
    resource "/arenas/{id}/delete" {
        name "ArenaDelete"
        requireAuth
        post Handlers.deleteGame
    }

let arenaSse =
    resource "/arenas/{id}/sse" {
        name "ArenaSse"
        datastar Handlers.gameSse
    }

// Sd: the discovery documents. Present on every cell as routes; 404 unless Sd is on, so the
// factor — not the routing table — decides whether the contract is discoverable.
let profileResource =
    resource "/profile" {
        name "Profile"
        requireAuth
        get Handlers.profile
    }

let wellKnownHomeResource =
    resource "/.well-known/home" {
        name "WellKnownHome"
        requireAuth
        get Handlers.wellKnownHome
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
    | "/games" | "/arenas" -> Some "POST, OPTIONS"
    | "/profile" | "/.well-known/home" -> Some "GET, OPTIONS"
    | p when p.StartsWith "/games/" || p.StartsWith "/arenas/" -> Some "GET, POST, DELETE, OPTIONS"
    | _ -> None

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

        plugBeforeRoutingWhen isDevelopment DeveloperExceptionPageExtensions.UseDeveloperExceptionPage

        plugBeforeRoutingWhenNot isDevelopment (fun app -> ExceptionHandlerExtensions.UseExceptionHandler(app, "/error", true))

        useAuthentication (fun auth -> auth.AddCookie(fun options -> options.Cookie.Name <- "TicTacToe.User"; options.Cookie.HttpOnly <- true; options.Cookie.SameSite <- SameSiteMode.Strict; options.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest; options.ExpireTimeSpan <- TimeSpan.FromDays(30.0); options.SlidingExpiration <- true; options.LoginPath <- "/login"))

        useAuthorization

        useOpenApi

        plugBeforeRouting ResponseCompressionBuilderExtensions.UseResponseCompression
        plugBeforeRouting StaticFileExtensions.UseStaticFiles
        // No antiforgery middleware: it only auto-validates endpoints carrying antiforgery
        // metadata (minimal-API form binding), which these manual ctx.Request.Form handlers do
        // not have — so it was a no-op giving a false sense of protection. CSRF on the no-JS
        // state-changing POSTs is mitigated by the SameSite=Strict auth cookie (see useAuthentication),
        // which blocks the cross-site requests an antiforgery token would otherwise guard.
        plugBeforeRouting createInitialGames
        plugBeforeRouting useOptionsDiscovery
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
        resource arenas
        resource arenaById
        resource arenaType
        resource arenaRestart
        resource arenaReset
        resource arenaDelete
        resource arenaSse
        resource profileResource
        resource wellKnownHomeResource
    }

    0
