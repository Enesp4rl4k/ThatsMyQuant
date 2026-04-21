using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A simulation service that provides a stream of global macroeconomic headlines.
/// In a production environment, this would be a socket consumer for Bloomberg Terminal
/// or a Twitter/X real-time API listener.
/// </summary>
public class NewsFeedSim
{
    private readonly ILogger<NewsFeedSim> _logger;
    private readonly bool _simulationMode;

    private static readonly string[] MacroEvents =
    [
        "FED Chairman signals potential interest rate cuts by Q3.",
        "Breaking: Major crypto exchange suffers $200M hot-wallet exploit.",
        "U.S. CPI data comes in at 3.1%, slightly higher than consensus.",
        "Geopolitical tensions rise as trade negotiations stall in the Middle East.",
        "SEC approves spot Ethereum ETF listing on major exchanges.",
        "Whale alert: $500M Bitcoin moved from cold storage to Binance."
    ];

    // Returned in paper/backtest mode — neutral so it never triggers a black-swan veto
    private const string NeutralHeadline = "Markets stable. No significant macro events detected.";

    private readonly Random _rng = new();

    public NewsFeedSim(ILogger<NewsFeedSim> logger, IConfiguration? configuration = null)
    {
        _logger = logger;
        var mode = configuration?["TMQ_MODE"] ?? configuration?["ExchangeMode"] ?? string.Empty;
        var exchange = configuration?["TMQ_EXCHANGE_MODE"] ?? string.Empty;
        _simulationMode = mode.Equals("paper", StringComparison.OrdinalIgnoreCase)
            || exchange.Equals("Mock", StringComparison.OrdinalIgnoreCase);
    }

    public void Reset()
    {
        _logger.LogInformation("[NEWS FEED] Resetting for a new session.");
    }

    public string GetLatestHeadline()
    {
        if (_simulationMode)
        {
            _logger.LogDebug("[NEWS FEED] Simulation mode — returning neutral headline.");
            return NeutralHeadline;
        }

        var headline = MacroEvents[_rng.Next(MacroEvents.Length)];
        _logger.LogInformation("[NEWS FEED] Published: {Headline}", headline);
        return headline;
    }
}
