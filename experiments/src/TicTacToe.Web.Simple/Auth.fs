namespace TicTacToe.Web.Simple

open System
open System.Security.Claims
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging

/// <summary>
/// Constants for claim types used in the application for user identification.
/// </summary>
[<RequireQualifiedAccess>]
module ClaimTypes =
    /// <summary>User identifier claim (subject)</summary>
    [<Literal>]
    let UserId = "sub"

    /// <summary>When the user was first created</summary>
    [<Literal>]
    let Created = "created_at"

    /// <summary>When the user last visited</summary>
    [<Literal>]
    let LastVisit = "last_visit"

    /// <summary>User's IP address (for diagnostics)</summary>
    [<Literal>]
    let IpAddress = "ip_address"

    /// <summary>User agent string (for diagnostics)</summary>
    [<Literal>]
    let UserAgent = "user_agent"

    /// <summary>Device identifier (for multiple device detection)</summary>
    [<Literal>]
    let DeviceId = "device_id"

    /// <summary>Game preference settings</summary>
    [<Literal>]
    let GamePreferences = "game_prefs"

/// <summary>
/// Extension methods for working with claims and principals.
/// </summary>
[<AutoOpen>]
module ClaimsExtensions =
    type ClaimsPrincipal with
        member this.FindClaimValue(claimType: string) =
            this.FindFirst(claimType)
            |> Option.ofObj
            |> Option.map (fun c -> c.Value)

        member this.HasClaim(claimType: string) =
            this.HasClaim(fun c -> c.Type = claimType)

        member this.TryGetUserId() =
            this.FindClaimValue(ClaimTypes.UserId)

        member this.GetAllClaims() =
            this.Claims
            |> Seq.map (fun c -> c.Type, c.Value)

    type ClaimsIdentity with
        member this.AddOrUpdateClaim(claimType: string, value: string) =
            let existing = this.FindFirst(claimType)
            if existing <> null then
                this.RemoveClaim(existing)
            this.AddClaim(Claim(claimType, value))

/// <summary>
/// Transforms the user identity by adding necessary claims for user identification.
/// </summary>
type GameUserClaimsTransformation(httpContextAccessor: IHttpContextAccessor, log: ILogger<GameUserClaimsTransformation>) =

    let generateUserId() =
        let id = Guid.NewGuid().ToString()
        log.LogDebug("Generated new user ID: {UserId}", id)
        id

    let getTimestamp() =
        DateTimeOffset.UtcNow.ToString("o")

    let createClaim (claimType: string) (value: string) =
        Claim(claimType, value)

    let captureEnvironmentalInfo() =
        try
            let context = httpContextAccessor.HttpContext
            if isNull context then [||] else

            [|
                if not (isNull context.Connection) &&
                    not (isNull context.Connection.RemoteIpAddress) &&
                    not (String.IsNullOrEmpty(context.Connection.RemoteIpAddress.ToString())) then
                    ClaimTypes.IpAddress, context.Connection.RemoteIpAddress.ToString()

                if not (isNull context.Request) &&
                   context.Request.Headers.ContainsKey("User-Agent") then
                    ClaimTypes.UserAgent, context.Request.Headers["User-Agent"].ToString()
            |]
        with ex ->
            log.LogWarning(ex, "Error capturing environmental information")
            [||]

    interface IClaimsTransformation with
        member _.TransformAsync(principal: ClaimsPrincipal) =
            task {
                try
                    if not (isNull log) then
                        let existingId = principal.FindClaimValue(ClaimTypes.UserId)
                        match existingId with
                        | Some id -> log.LogDebug("Transforming claims for existing user {UserId}", id)
                        | None -> log.LogDebug("Transforming claims for new user")

                    if principal.HasClaim(ClaimTypes.UserId) &&
                       principal.HasClaim(ClaimTypes.Created) then

                        let identity =
                            match principal.Identity with
                            | null ->
                                log.LogWarning("Principal has null identity, creating new one")
                                new ClaimsIdentity("TicTacToe.User")
                            | identity -> ClaimsIdentity(identity)

                        identity.AddOrUpdateClaim(ClaimTypes.LastVisit, getTimestamp())

                        return ClaimsPrincipal(identity)
                    else
                        let claims = ResizeArray<Claim>()

                        let userId =
                            principal.FindClaimValue(ClaimTypes.UserId)
                            |> Option.defaultWith generateUserId
                        claims.Add(createClaim ClaimTypes.UserId userId)

                        let timestamp = getTimestamp()
                        claims.Add(createClaim ClaimTypes.Created timestamp)
                        claims.Add(createClaim ClaimTypes.LastVisit timestamp)

                        for (claimType, value) in captureEnvironmentalInfo() do
                            claims.Add(createClaim claimType value)

                        let identity = ClaimsIdentity(claims, "TicTacToe.User")
                        let newPrincipal = ClaimsPrincipal(identity)

                        log.LogInformation("Created new user identity with ID {UserId}", userId)

                        return newPrincipal
                with ex ->
                    log.LogError(ex, "Error during claims transformation")
                    return principal
            }

    member _.CurrentContext = httpContextAccessor.HttpContext
