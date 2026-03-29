open System
open System.IO.Compression
open System.Security.Claims
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
open Frank.Statecharts
open Frank.Affordances
open TicTacToe.Web
open TicTacToe.Web.Model
open TicTacToe.Web.GameStateMachine
open TicTacToe.Engine
open TicTacToe.Web.Extensions

let configureLogging (builder: ILoggingBuilder) =
    builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
    builder.AddFilter("TicTacToe.Web.Auth", LogLevel.Information) |> ignore
    builder

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddHttpContextAccessor() |> ignore

    services.AddAntiforgery() |> ignore

    // Add statechart store for GamePhase tracking
    services.AddStateMachineStore<GamePhase, unit>() |> ignore

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

// ============================================================================
// State key resolver middleware
// ============================================================================

/// Resolves the statechart state key from the store and sets IStatechartFeature
/// on HttpContext.Features. Also enriches the user's claims with player assignment
/// for the current game so role predicates and guards can check them.
/// Must run AFTER routing and BEFORE the statechart middleware.
///
/// Enforces authentication for stateful resource endpoints: unauthenticated
/// requests are challenged (302 redirect to login) before reaching the
/// statechart middleware or any handler.
let resolveStateKey (app: IApplicationBuilder) =
    app.Use(
        Func<HttpContext, Func<Task>, Task>(fun ctx next ->
            task {
                let endpoint = ctx.GetEndpoint()
                let mutable challenged = false

                if not (isNull endpoint) then
                    let metadata = endpoint.Metadata.GetMetadata<StateMachineMetadata>()

                    if not (obj.ReferenceEquals(metadata, null)) then
                        // Enforce authentication — statefulResource endpoints require auth
                        if not ctx.User.Identity.IsAuthenticated then
                            do! ctx.ChallengeAsync()
                            challenged <- true
                        else
                            let instanceId = metadata.ResolveInstanceId ctx

                            // Enrich user claims with player assignment for this game
                            let assignmentManager =
                                ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
                            let assignment = assignmentManager.GetAssignment(instanceId)
                            let userId = ctx.User.TryGetUserId()

                            match assignment, userId with
                            | Some a, Some uid ->
                                let claims = ResizeArray<Claim>()
                                if a.PlayerXId = Some uid then
                                    claims.Add(Claim("player", "X"))
                                elif a.PlayerOId = Some uid then
                                    claims.Add(Claim("player", "O"))
                                if claims.Count > 0 then
                                    let identity = ClaimsIdentity(claims, "GameAssignment")
                                    ctx.User.AddIdentity(identity)
                            | _ -> ()

                            let! _stateKey = metadata.GetCurrentStateKey ctx.RequestServices ctx instanceId
                            ()

                if not challenged then
                    do! next.Invoke()
            }
            :> Task)
    )

// ============================================================================
// Resources
// ============================================================================

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

/// Adapter: upcast Task<unit> -> Task for StateHandlerBuilder compatibility.
let inline asTask (handler: HttpContext -> Task<unit>) : HttpContext -> Task =
    fun ctx -> handler ctx :> Task

/// Stateful game resource — the statechart middleware handles state-dependent
/// routing, method checks, and guard evaluation (turn order via claims).
let gameById =
    statefulResource "/games/{id}" {
        machine gameMachine
        resolveInstanceId (fun ctx -> ctx.Request.RouteValues.["id"] |> string)
        role "PlayerX" (fun user -> user.HasClaim("player", "X"))
        role "PlayerO" (fun user -> user.HasClaim("player", "O"))
        role "Spectator" (fun _user -> true)
        inState (forState GamePhase.XTurn [ StateHandlerBuilder.get (asTask Handlers.getGame); StateHandlerBuilder.post (asTask Handlers.makeMove); StateHandlerBuilder.delete (asTask Handlers.deleteGame) ])
        inState (forState GamePhase.OTurn [ StateHandlerBuilder.get (asTask Handlers.getGame); StateHandlerBuilder.post (asTask Handlers.makeMove); StateHandlerBuilder.delete (asTask Handlers.deleteGame) ])
        inState (forState GamePhase.Won [ StateHandlerBuilder.get (asTask Handlers.getGame); StateHandlerBuilder.delete (asTask Handlers.deleteGame) ])
        inState (forState GamePhase.Draw [ StateHandlerBuilder.get (asTask Handlers.getGame); StateHandlerBuilder.delete (asTask Handlers.deleteGame) ])
        inState (forState GamePhase.Error [ StateHandlerBuilder.get (asTask Handlers.getGame) ])
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

    let store =
        app.ApplicationServices.GetRequiredService<IStateMachineStore<GamePhase, unit>>()

    lifetime.ApplicationStarted.Register(fun () ->
        // Create 6 initial games
        for _ in 1..6 do
            let (gameId, game) = supervisor.CreateGame()
            // Seed initial state in the statechart store
            store.SetState gameId GamePhase.XTurn () |> fun t -> t.Wait()
            Handlers.subscribeToGame gameId game assignmentManager supervisor store)
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

        plugBeforeRouting ResponseCompressionBuilderExtensions.UseResponseCompression
        plugBeforeRouting StaticFileExtensions.UseStaticFiles
        plugBeforeRouting AntiforgeryApplicationBuilderExtensions.UseAntiforgery
        plugBeforeRouting createInitialGames

        // Statechart middleware pipeline (after routing, before endpoint execution):
        // 1. State key resolver (reads store, enriches claims, sets IStatechartFeature)
        plug resolveStateKey
        // 2. Affordance middleware (reads IStatechartFeature, injects Link + Allow headers)
        useAffordances
        // 3. Projected profile middleware (role-specific ALPS profile links)
        useProjectedProfiles
        // 4. Statechart middleware (state-dependent handler dispatch)
        useStatecharts

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
