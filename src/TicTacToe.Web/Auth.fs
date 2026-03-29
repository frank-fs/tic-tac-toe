namespace TicTacToe.Web

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
        /// <summary>
        /// Get the value of a specific claim type as an option.
        /// </summary>
        /// <param name="claimType">The type of claim to find</param>
        /// <returns>The claim value as an option, or None if not found</returns>
        member this.FindClaimValue(claimType: string) =
            this.FindFirst(claimType)
            |> Option.ofObj
            |> Option.map (fun c -> c.Value)

        /// <summary>
        /// Check if the principal has a claim of the specified type.
        /// </summary>
        /// <param name="claimType">The type of claim to check for</param>
        /// <returns>True if the claim exists, false otherwise</returns>
        member this.HasClaim(claimType: string) =
            this.HasClaim(fun c -> c.Type = claimType)

        /// <summary>
        /// Try to get the user ID from the principal.
        /// </summary>
        /// <returns>The user ID as an option, or None if not found</returns>
        member this.TryGetUserId() =
            this.FindClaimValue(ClaimTypes.UserId)

        /// <summary>
        /// Get all claims as a sequence of key-value pairs.
        /// </summary>
        /// <returns>A sequence of tuples containing claim type and value</returns>
        member this.GetAllClaims() =
            this.Claims
            |> Seq.map (fun c -> c.Type, c.Value)

    /// <summary>
    /// Extension methods for ClaimsIdentity
    /// </summary>
    type ClaimsIdentity with
        /// <summary>
        /// Add a claim if it doesn't already exist, otherwise update its value.
        /// </summary>
        /// <param name="claimType">The type of claim</param>
        /// <param name="value">The claim value</param>
        member this.AddOrUpdateClaim(claimType: string, value: string) =
            let existing = this.FindFirst(claimType)
            if existing <> null then
                this.RemoveClaim(existing)
            this.AddClaim(Claim(claimType, value))

/// <summary>
/// Transforms the user identity by adding necessary claims for user identification.
/// This implementation ensures each user gets a persistent identity without requiring sign-in.
/// </summary>
type GameUserClaimsTransformation(httpContextAccessor: IHttpContextAccessor, log: ILogger<GameUserClaimsTransformation>) =

    /// <summary>
    /// Generates a new random user ID using a GUID
    /// </summary>
    let generateUserId() =
        let id = Guid.NewGuid().ToString()
        log.LogDebug("Generated new user ID: {UserId}", id)
        id

    /// <summary>
    /// Gets the current timestamp as ISO 8601 string
    /// </summary>
    let getTimestamp() =
        DateTimeOffset.UtcNow.ToString("o")

    /// <summary>
    /// Creates a new claim with the given type and value
    /// </summary>
    let createClaim (claimType:string) (value: string) =
        Claim(claimType, value)

    /// <summary>
    /// Captures environmental information to enhance user identification
    /// </summary>
    let captureEnvironmentalInfo() =
        try
            let context = httpContextAccessor.HttpContext
            if isNull context then [||] else

            [|
                // Capture IP address if available
                if not (isNull context.Connection) &&
                    not (isNull context.Connection.RemoteIpAddress) &&
                    not (String.IsNullOrEmpty(context.Connection.RemoteIpAddress.ToString())) then
                    ClaimTypes.IpAddress, context.Connection.RemoteIpAddress.ToString()

                // Capture User-Agent if available
                if not (isNull context.Request) &&
                   context.Request.Headers.ContainsKey("User-Agent") then
                    ClaimTypes.UserAgent, context.Request.Headers["User-Agent"].ToString()
            |]
        with ex ->
            log.LogWarning(ex, "Error capturing environmental information")
            [||]

    /// <summary>
    /// Tries to extract an agent identity from the X-Agent-Id header.
    /// Returns Some(ClaimsPrincipal) if the header is present and non-empty.
    /// </summary>
    let tryCreateAgentPrincipal () =
        try
            let context = httpContextAccessor.HttpContext
            if isNull context then None
            else
                match context.Request.Headers.TryGetValue("X-Agent-Id") with
                | true, values ->
                    let agentId = values.ToString()
                    if String.IsNullOrWhiteSpace(agentId) then None
                    else
                        log.LogInformation("Agent authentication via X-Agent-Id: {AgentId}", agentId)
                        let claims = [|
                            Claim(ClaimTypes.UserId, agentId)
                            Claim(ClaimTypes.Created, getTimestamp())
                            Claim(ClaimTypes.LastVisit, getTimestamp())
                        |]
                        let identity = ClaimsIdentity(claims, "TicTacToe.Agent")
                        Some(ClaimsPrincipal(identity))
                | _ -> None
        with ex ->
            log.LogWarning(ex, "Error checking X-Agent-Id header")
            None

    interface IClaimsTransformation with
        member _.TransformAsync(principal: ClaimsPrincipal) =
            task {
                try
                    // Check for agent identity via X-Agent-Id header first
                    // Only use agent identity if no existing authentication is present
                    match tryCreateAgentPrincipal() with
                    | Some agentPrincipal when not (principal.Identity <> null && principal.Identity.IsAuthenticated) ->
                        return agentPrincipal
                    | _ ->

                    // Log entry point with any existing user ID
                    if not (isNull log) then
                        let existingId = principal.FindClaimValue(ClaimTypes.UserId)
                        match existingId with
                        | Some id -> log.LogDebug("Transforming claims for existing user {UserId}", id)
                        | None -> log.LogDebug("Transforming claims for new user")

                    // If the principal already has all the claims we need, update the LastVisit timestamp
                    if principal.HasClaim(ClaimTypes.UserId) &&
                       principal.HasClaim(ClaimTypes.Created) then

                        // Create a new identity based on the existing one
                        let identity =
                            match principal.Identity with
                            | null ->
                                log.LogWarning("Principal has null identity, creating new one")
                                new ClaimsIdentity("TicTacToe.User")
                            | identity -> ClaimsIdentity(identity)

                        // Update LastVisit claim
                        identity.AddOrUpdateClaim(ClaimTypes.LastVisit, getTimestamp())

                        // Return the updated principal
                        return ClaimsPrincipal(identity)
                    else
                        // Create a new identity with the required claims
                        let claims = ResizeArray<Claim>()

                        // Add or reuse the user ID claim
                        let userId =
                            principal.FindClaimValue(ClaimTypes.UserId)
                            |> Option.defaultWith generateUserId
                        claims.Add(createClaim ClaimTypes.UserId userId)

                        // Add timestamps
                        let timestamp = getTimestamp()
                        claims.Add(createClaim ClaimTypes.Created timestamp)
                        claims.Add(createClaim ClaimTypes.LastVisit timestamp)

                        // Add environmental claims for additional identification factors
                        for (claimType, value) in captureEnvironmentalInfo() do
                            claims.Add(createClaim claimType value)

                        // Create new identity and principal
                        let identity = ClaimsIdentity(claims, "TicTacToe.User")
                        let newPrincipal = ClaimsPrincipal(identity)

                        // Log the successful creation of a new identity
                        log.LogInformation("Created new user identity with ID {UserId}", userId)

                        return newPrincipal
                with ex ->
                    // Log error but don't fail the transformation
                    log.LogError(ex, "Error during claims transformation")

                    // In case of error, return the original principal to avoid authentication failures
                    // This ensures the app remains functional even if identification has issues
                    return principal
            }

    /// <summary>
    /// Gets the current HTTP context
    /// </summary>
    member _.CurrentContext = httpContextAccessor.HttpContext
