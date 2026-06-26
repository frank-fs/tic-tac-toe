open System
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Frank.Builder
open Frank.Auth
open Frank.OpenApi
open TicTacToe.Web.Surface
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

        useOpenApi

        plugBeforeRouting StaticFileExtensions.UseStaticFiles
        plugBeforeRouting AntiforgeryApplicationBuilderExtensions.UseAntiforgery
        plugBeforeRouting createInitialArenas

        resource login
        resource logout
        resource home
        resource arenas
        resource arenaById
        resource arenaRestart
        resource arenaDelete
    }

    0
