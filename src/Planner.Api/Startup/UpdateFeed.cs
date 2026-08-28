using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Planner.Api.Startup;

public sealed class UpdateFeedOptions
{
    public const string SectionName = "Planner:Updates";

    /// <summary>Directory holding the Velopack release files that <c>build/release.ps1</c> produces.
    /// In compose this is a read-only bind mount of the host's <c>./releases</c> folder.</summary>
    public string Directory { get; set; } = "/var/lib/planner/updates";

    /// <summary>Public path the desktop client points at. Also the default the client derives from its
    /// server URL, so a standard install needs no client-side configuration at all.</summary>
    public string RequestPath { get; set; } = "/updates";

    public bool Enabled { get; set; } = true;
}

public static class UpdateFeed
{
    /// <summary>Serves the desktop client's update feed as static files.
    ///
    /// Deliberately anonymous. The client checks for updates before anyone signs in — that is the whole
    /// point of it, since a release that broke sign-in has to be replaceable — so requiring a token here
    /// would defeat the feature. What is exposed is the same set of installers you would hand out on a
    /// share: no customer data, and every package is signed by whatever certificate you pack with.</summary>
    public static WebApplication MapUpdateFeed(this WebApplication app, UpdateFeedOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Planner.UpdateFeed");

        if (!options.Enabled)
        {
            logger.LogInformation("Update feed disabled by configuration");
            return app;
        }

        try
        {
            System.IO.Directory.CreateDirectory(options.Directory);
        }
        catch (Exception ex)
        {
            // A read-only mount that does not exist yet is a deployment mistake, not a reason to refuse
            // to start: the rest of the API is unaffected.
            logger.LogWarning(ex, "Update feed directory {Directory} is not usable", options.Directory);
            return app;
        }

        // .nupkg and .json are not in the default MIME map, and an unmapped type is otherwise a 404.
        // Only release artefacts live in this directory, so serving unknown types is bounded.
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".nupkg"] = "application/octet-stream";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(options.Directory),
            RequestPath = options.RequestPath,
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
            OnPrepareResponse = context =>
            {
                // The feed index changes with every release and must never be served stale. Packages
                // carry their version in the filename and never change, so they cache indefinitely.
                var isIndex = context.File.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

                context.Context.Response.Headers.CacheControl = isIndex
                    ? "no-cache, no-store, must-revalidate"
                    : "public, max-age=31536000, immutable";
            }
        });

        logger.LogInformation(
            "Serving the client update feed from {Directory} at {Path}", options.Directory, options.RequestPath);

        return app;
    }
}
