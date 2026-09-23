namespace Planner.Api.Auth;

public sealed class PlannerAuthOptions
{
    public const string SectionName = "Planner:Auth";

    /// <summary>Canonical public issuer, e.g. https://planner.lyste.net/. Set behind reverse proxies
    /// so token issuance and validation do not depend on the scheme/host of each transport.
    /// When omitted, local installations derive the issuer from the request.</summary>
    public string? Issuer { get; set; }

    /// <summary>Directory holding the token signing/encryption certificates. Mount this as a volume so
    /// tokens stay valid across container restarts.</summary>
    public string KeyDirectory { get; set; } = "/var/lib/planner/keys";

    /// <summary>Password protecting the generated PFX files at rest.</summary>
    public string KeyPassword { get; set; } = "planner-dev-keys";

    public int AccessTokenMinutes { get; set; } = 60;

    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Client id the desktop and any other first-party app authenticates with. Public client:
    /// a desktop binary cannot keep a secret, so it does not get one.</summary>
    public string DesktopClientId { get; set; } = "planner-desktop";

    /// <summary>Client id the browser client authenticates with. Public for the same reason and more
    /// so: a single-page application is source anyone can read. It is registered separately from the
    /// desktop client so the two can be told apart in the logs and revoked independently.</summary>
    public string WebClientId { get; set; } = "planner-web";

    /// <summary>Allows plain HTTP on the token endpoint. Correct behind a TLS-terminating reverse
    /// proxy or for local evaluation; leave false when the API is exposed directly.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>How many reverse proxies stand between a client and this API. Zero means none, and the
    /// forwarded headers are ignored — correct for local runs and for an API exposed directly.
    ///
    /// It matters beyond tidy logs: the login rate limiter partitions by remote IP, and behind an
    /// unread X-Forwarded-For every request on earth shares one bucket, so the whole internet gets
    /// twenty sign-in attempts a minute between them. On the VPS the chain is Coolify's proxy and then
    /// this stack's web container: two hops.</summary>
    public int TrustedProxyHops { get; set; }

    /// <summary>Origins allowed to call the API from a browser. The web client is served from this
    /// API's own origin, so this exists only for a front end hosted elsewhere and for tooling such as
    /// Scalar.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>Scopes this API understands.</summary>
public static class PlannerScopes
{
    public const string Api = "planner.api";
}
