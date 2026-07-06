open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Frank.Builder
open Frank.Auth
open TicTacToe.Web.Surface
open TicTacToe.Web.Surface.Surface
open TicTacToe.Web.Surface.GameStore
open TicTacToe.Web.Surface.Model
open TicTacToe.Web.Surface.Logger
open TicTacToe.Web.Surface.Extensions

let private initialGames () =
    match System.Environment.GetEnvironmentVariable("TICTACTOE_INITIAL_GAMES") with
    | null | "" -> 6
    | s ->
        match System.Int32.TryParse(s) with
        | true, n when n > 0 -> n
        | _ -> 6

let private maxGames () =
    match System.Environment.GetEnvironmentVariable("TICTACTOE_MAX_GAMES") with
    | null | "" -> None
    | s ->
        match System.Int32.TryParse(s) with
        | true, n when n > 0 -> Some n
        | _ -> None

let private requestLogPath () =
    match System.Environment.GetEnvironmentVariable("TICTACTOE_REQUEST_LOG_PATH") with
    | null | "" -> None
    | s -> Some s

let configureLogging (builder: ILoggingBuilder) =
    builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
    builder.AddFilter("TicTacToe.Web.Surface", LogLevel.Information) |> ignore
    builder

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddHttpContextAccessor() |> ignore
    services.AddAntiforgery() |> ignore

    services
        .AddSingleton<Surface>(Surface.fromEnvironment())
        .AddSingleton<GameStore>(fun _ -> GameStore(?maxGames = maxGames()))
        .AddSingleton<RequestLogger>(fun _ -> RequestLogger(?logPath = requestLogPath()))
        .AddSingleton<PlayerAssignmentManager>(fun _ -> PlayerAssignmentManager())
        .AddSingleton<IClaimsTransformation, GameUserClaimsTransformation>()
    |> ignore

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

let home =
    resource "/" {
        name "Home"
        requireAuth
        get Handlers.home
    }

let arenas =
    resource "/arenas" {
        name "Arenas"
        requireAuth
        post Handlers.createArena
    }

let arenaById =
    resource "/arenas/{id}" {
        name "ArenaById"
        requireAuth
        get Handlers.getArena
        post Handlers.makeMove
        delete Handlers.deleteArena
    }

let arenaRestart =
    resource "/arenas/{id}/restart" {
        name "ArenaRestart"
        requireAuth
        post Handlers.restartArena
    }

// HTML-form DELETE workaround (browsers cannot send DELETE from forms)
let arenaDelete =
    resource "/arenas/{id}/delete" {
        name "ArenaDelete"
        requireAuth
        post Handlers.deleteArena
    }

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

let arenaType =
    resource "/arenas/{id}/type" {
        name "ArenaType"
        requireAuth
        get Handlers.arenaType
    }

let private optionsAllow (path: string) =
    match path with
    | "/" -> Some "GET, OPTIONS"
    | "/arenas" -> Some "POST, OPTIONS"
    | "/profile" | "/.well-known/home" -> Some "GET, OPTIONS"
    | p when p.StartsWith "/arenas/" -> Some "GET, POST, DELETE, OPTIONS"
    | _ -> None

let useOptionsDiscovery (app: IApplicationBuilder) =
    let surface = app.ApplicationServices.GetRequiredService<Surface>()
    if not surface.Sd then app
    else
        app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
            task {
                if ctx.Request.Method = "OPTIONS" then
                    match optionsAllow ctx.Request.Path.Value with
                    | Some allow ->
                        ctx.Response.StatusCode <- 204
                        ctx.Response.Headers.Append("Allow", allow)
                    | None -> do! next.Invoke ctx
                else
                    do! next.Invoke ctx
            } :> Task)

/// Create initial arenas on application startup
let createInitialArenas (app: IApplicationBuilder) =
    let lifetime =
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>()

    let store = app.ApplicationServices.GetRequiredService<GameStore>()

    lifetime.ApplicationStarted.Register(fun () ->
        for _ in 1..initialGames() do
            store.Create() |> ignore
        )
    |> ignore

    app

[<EntryPoint>]
let main args =
    webHost args {
        useDefaults

        service configureServices

        logging configureLogging

        plugBeforeRoutingWhen isDevelopment DeveloperExceptionPageExtensions.UseDeveloperExceptionPage

        plugBeforeRoutingWhenNot isDevelopment (fun app ->
            ExceptionHandlerExtensions.UseExceptionHandler(app, "/error", true))

        useAuthentication (fun auth ->
            auth.AddCookie(fun options ->
                options.Cookie.Name <- "TicTacToe.SimpleUser"
                options.Cookie.HttpOnly <- true
                options.Cookie.SameSite <- SameSiteMode.Strict
                options.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest
                options.ExpireTimeSpan <- TimeSpan.FromDays(30.0)
                options.SlidingExpiration <- true
                options.LoginPath <- "/login"))

        useAuthorization

        plugBeforeRouting StaticFileExtensions.UseStaticFiles
        plugBeforeRouting AntiforgeryApplicationBuilderExtensions.UseAntiforgery
        plugBeforeRouting createInitialArenas
        plugBeforeRouting useOptionsDiscovery

        resource login
        resource logout
        resource home
        resource arenas
        resource arenaById
        resource arenaType
        resource arenaRestart
        resource arenaDelete
        resource profileResource
        resource wellKnownHomeResource
    }

    0
