namespace Listener.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// When false (default), all endpoints stay open — preserves the existing
    /// X-Api-Key flow. When true, JWT bearer auth is enforced on every endpoint
    /// not explicitly tagged AllowAnonymous (login + health probes).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>HMAC-SHA256 signing key. Auto-generated and persisted on first run if blank.</summary>
    public string JwtSecret { get; set; } = string.Empty;

    public string Issuer { get; set; } = "ThatsMyQuant";
    public string Audience { get; set; } = "ThatsMyQuant.Web";

    /// <summary>Access token lifetime. 12h matches a normal trading shift; user re-logs once a day.</summary>
    public int TokenLifetimeHours { get; set; } = 12;
}
