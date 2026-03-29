module TicTacToe.Web.Extensions

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Frank.Builder

/// Helper function to determine whether the application
/// is running in a development environment.
let isDevelopment (app: IApplicationBuilder) = app.ApplicationServices.GetService<IWebHostEnvironment>().IsDevelopment()

type WebHostBuilder with

    /// Extension to the WebHostBuilder computation expression
    /// to support logging through the ILoggingBuilder.
    [<CustomOperation("logging")>]
    member __.Logging(spec: WebHostSpec, f: ILoggingBuilder -> ILoggingBuilder) =
        { spec with
            Services = fun services -> spec.Services(services).AddLogging(fun builder -> f builder |> ignore)
        }

type ResourceBuilder with

    /// Adds DiscoveryMediaType metadata for text/html to all endpoints on this resource.
    /// This enables the OPTIONS and Link header discovery middlewares to advertise HTML support.
    [<CustomOperation("discoveryMediaType")>]
    member _.DiscoveryMediaType(spec: ResourceSpec, mediaType: string, rel: string) : ResourceSpec =
        ResourceBuilder.AddMetadata(spec, fun b ->
            b.Metadata.Add({ MediaType = mediaType; Rel = rel }: DiscoveryMediaType))
