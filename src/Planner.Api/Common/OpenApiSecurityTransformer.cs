using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Planner.Api.Common;

/// <summary>Describes the OAuth password flow in the generated document so the Scalar UI can obtain a
/// token and call the API without anyone hand-crafting a curl command first.</summary>
public sealed class OpenApiSecurityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Planner API",
            Version = "v1",
            Description =
                "On-premises project and issue tracking. Obtain a token from /connect/token using the " +
                "password grant with client_id=planner-desktop, then send it as a bearer token. " +
                "Live changes are pushed over SignalR at /hubs/planner."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT access token issued by /connect/token."
        };

        return Task.CompletedTask;
    }
}
