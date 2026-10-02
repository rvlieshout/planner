namespace Planner.Api.Common;

public static class SecurityHeaders
{
    /// <summary>What every response says about how a browser may treat it: take the declared content type
    /// as given, never show it inside another site's frame, and tell no other site where a link was
    /// followed from. Almost everything here is JSON, where none of this matters; it is for the few
    /// pages the API does serve, and for a download opened straight from its address.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            return next(context);
        });
}
