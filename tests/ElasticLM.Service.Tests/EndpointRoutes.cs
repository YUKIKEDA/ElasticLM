using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Reads the routes a running host registered, as the reference pages write them (design §20.7, V1c2b). The service tests and
/// the server tests link this file.
/// </summary>
internal static class EndpointRoutes
{
    /// <summary>
    /// Returns every route of a host's endpoints: the method, one space, and the path with a leading '/', such as
    /// <c>DELETE /v1/leases/{id}</c>. An endpoint with several methods gives one route for each.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <returns>The routes, sorted and without duplicates.</returns>
    public static IReadOnlyList<string> Of(IServiceProvider services)
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (RouteEndpoint endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            string raw = endpoint.RoutePattern.RawText ?? throw new InvalidOperationException("A route has no pattern text.");
            string path = raw.StartsWith('/') ? raw : "/" + raw;
            IReadOnlyList<string> methods =
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                ?? throw new InvalidOperationException($"The route {path} accepts every method; a reference page names one method per route.");
            routes.UnionWith(methods.Select(method => method + " " + path));
        }

        return [.. routes];
    }
}
