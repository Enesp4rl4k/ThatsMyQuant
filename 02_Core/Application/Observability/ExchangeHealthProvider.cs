namespace ThatsMyQuant.Application.Observability;

public sealed record ExchangeHealthSnapshot(
    string Venue,
    bool CredentialsConfigured,
    bool IsConnected,
    bool IsDemoMode,
    decimal? Balance,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    string? LastError,
    int ConsecutiveFailures);

/// <summary>
/// Tracks the live authentication / reachability status of the configured
/// exchange (OKX, Binance, Mock). Populated by <c>ExchangeHealthProbeHostedService</c>
/// and surfaced via the Worker's management API <c>/exchange/health</c> endpoint
/// so the Dashboard can show a clearly-visible OKX status badge.
/// </summary>
public interface IExchangeHealthProvider
{
    ExchangeHealthSnapshot Current { get; }
    void RecordSuccess(string venue, bool isDemoMode, decimal balance);
    void RecordFailure(string venue, bool isDemoMode, string error);
    void MarkCredentialsMissing(string venue);
}

public sealed class ExchangeHealthProvider : IExchangeHealthProvider
{
    private readonly object _gate = new();
    private ExchangeHealthSnapshot _current = new(
        Venue: "unknown",
        CredentialsConfigured: false,
        IsConnected: false,
        IsDemoMode: false,
        Balance: null,
        LastSuccessUtc: null,
        LastAttemptUtc: null,
        LastError: "Not probed yet.",
        ConsecutiveFailures: 0);

    public ExchangeHealthSnapshot Current
    {
        get { lock (_gate) { return _current; } }
    }

    public void RecordSuccess(string venue, bool isDemoMode, decimal balance)
    {
        lock (_gate)
        {
            _current = new ExchangeHealthSnapshot(
                Venue: venue,
                CredentialsConfigured: true,
                IsConnected: true,
                IsDemoMode: isDemoMode,
                Balance: balance,
                LastSuccessUtc: DateTime.UtcNow,
                LastAttemptUtc: DateTime.UtcNow,
                LastError: null,
                ConsecutiveFailures: 0);
        }
    }

    public void RecordFailure(string venue, bool isDemoMode, string error)
    {
        lock (_gate)
        {
            _current = _current with
            {
                Venue = venue,
                CredentialsConfigured = true,
                IsConnected = false,
                IsDemoMode = isDemoMode,
                LastAttemptUtc = DateTime.UtcNow,
                LastError = Truncate(error, 400),
                ConsecutiveFailures = _current.ConsecutiveFailures + 1
            };
        }
    }

    public void MarkCredentialsMissing(string venue)
    {
        lock (_gate)
        {
            _current = new ExchangeHealthSnapshot(
                Venue: venue,
                CredentialsConfigured: false,
                IsConnected: false,
                IsDemoMode: false,
                Balance: null,
                LastSuccessUtc: null,
                LastAttemptUtc: DateTime.UtcNow,
                LastError: "Credentials not configured.",
                ConsecutiveFailures: 0);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");
}
