open System
open System.IO.Compression
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.ResponseCompression
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Frank.Builder
open Frank.Auth
open Frank.Datastar
open Frank.Discovery
open Frank.Affordances
open TicTacToe.Web
open TicTacToe.Web.Model
open TicTacToe.Engine
open TicTacToe.Web.Extensions

let configureLogging (builder: ILoggingBuilder) =
    builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
    builder.AddFilter("TicTacToe.Web.Auth", LogLevel.Information) |> ignore
    builder

let configureServices (services: IServiceCollection) =
    services.AddSingleton<JsonHomeMetadata>(
        { Title = Some "TicTacToe"
          DocsUrl = None
          AlpsBaseUri = None
          AlpsDescriptors = None })
    |> ignore

    services.AddRouting().AddHttpContextAccessor() |> ignore

    services.AddAntiforgery() |> ignore

    services
        .AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor ())
        .AddSingleton<PlayerAssignmentManager>(fun _ -> PlayerAssignmentManager())
        .AddSingleton<IClaimsTransformation, GameUserClaimsTransformation>()
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
        entryPoint
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
        entryPoint
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

/// Create initial games on application startup
let createInitialGames (app: IApplicationBuilder) =
    let lifetime =
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>()

    let supervisor = app.ApplicationServices.GetRequiredService<GameSupervisor>()

    let assignmentManager =
        app.ApplicationServices.GetRequiredService<PlayerAssignmentManager>()

    lifetime.ApplicationStarted.Register(fun () ->
        // Create 6 initial games
        for _ in 1..6 do
            let (gameId, game) = supervisor.CreateGame()
            Handlers.subscribeToGame gameId game assignmentManager supervisor)
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

        useDiscovery

        useAuthentication (fun auth -> auth.AddCookie(fun options -> options.Cookie.Name <- "TicTacToe.User"; options.Cookie.HttpOnly <- true; options.Cookie.SameSite <- SameSiteMode.Strict; options.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest; options.ExpireTimeSpan <- TimeSpan.FromDays(30.0); options.SlidingExpiration <- true; options.LoginPath <- "/login"; options.Events <- CookieAuthenticationEvents(OnRedirectToLogin = fun ctx -> if ctx.Request.Headers.ContainsKey("X-Agent-Id") then ctx.Response.StatusCode <- 401; ctx.Response.Headers["WWW-Authenticate"] <- "X-Agent-Id realm=\"TicTacToe\""; Task.CompletedTask else ctx.Response.Redirect(ctx.RedirectUri); Task.CompletedTask)))

        useAuthorization

        plugBeforeRouting ResponseCompressionBuilderExtensions.UseResponseCompression
        plugBeforeRouting StaticFileExtensions.UseStaticFiles
        plugBeforeRouting AntiforgeryApplicationBuilderExtensions.UseAntiforgery
        plugBeforeRouting createInitialGames

        resource login
        resource logout
        resource debug
        resource home
        resource sse
        resource games
        resource gameById
        resource gameReset
    }

    0
