using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Application.Strategy;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Validators;

public class MarketHoursValidator : ISignalValidator
{
    private readonly ILogger<MarketHoursValidator> _logger;

    public MarketHoursValidator(ILogger<MarketHoursValidator> logger)
    {
        _logger = logger;
    }

    public int Order => 0;

    public Task<(bool IsValid, string FailureReason)> ValidateAsync(Signal signal)
    {
        var market = signal.MarketType.ToString().ToUpper();
        var session = MarketHoursScheduler.GetCurrentSession(market);

        if (session.CanTrade) return Task.FromResult((true, string.Empty));

        var reason = $"Outside {market} trading hours: {session.Description}";
        _logger.LogWarning("Validation Failed: {Reason}", reason);
        return Task.FromResult((false, reason));
    }
}
