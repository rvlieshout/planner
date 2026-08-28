namespace Planner.Api.Auth;

public sealed class PlannerAuthOptions
{
    public const string SectionName = "Planner:Auth";

    /// <summary>Directory holding the token signing/encryption certificates. Mount this as a volume so
    /// tokens stay valid across container restarts.</summary>
    public string KeyDirectory { get; set; } = "/var/lib/planner/keys";

    /// <summary>Password protecting the generated PFX files at rest.</summary>
    public string KeyPassword { get; set; } = "planner-dev-keys";

    public int AccessTokenMinutes { get; set; } = 60;

    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Client id the desktop and any other first-party app authenticates with. Public client:
    /// a desktop binary cannot keep a secret, so it does not get one.</summary>
    public string DesktopClientId { get; set; } = "planner-desktop";

    /// <summary>Allows plain HTTP on the token endpoint. Correct behind a TLS-terminating reverse
    /// proxy or for local evaluation; leave false when the API is exposed directly.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Origins allowed to call the API from a browser. The Avalonia client is not subject to
    /// CORS; this exists for a future web front end and for tooling such as Scalar.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>Scopes this API understands.</summary>
public static class PlannerScopes
{
    public const string Api = "planner.api";
}
