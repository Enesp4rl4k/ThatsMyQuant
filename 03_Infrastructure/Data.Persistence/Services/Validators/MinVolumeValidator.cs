using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Validators;

public class MinVolumeValidator : ISignalValidator
{
    private readonly ILogger<MinVolumeValidator> _logger;
    private const decimal MinVolumeThreshold = 1_000_000m; // $1M minimum ADV

    public MinVolumeValidator(ILogger<MinVolumeValidator> logger)
    {
        _logger = logger;
    }

    public int Order => 10;

    public Task<(bool IsValid, string FailureReason)> ValidateAsync(Signal signal)
    {
        if (signal.Volume24h == 0)
            return Task.FromResult((true, string.Empty));

        if (signal.Volume24h < MinVolumeThreshold)
        {
            var reason = $"Low liquidity detected: 24h Volume ${signal.Volume24h:N0} is below threshold ${MinVolumeThreshold:N0}.";
            _logger.LogWarning("Validation Failed: {Reason}", reason);
            return Task.FromResult((false, reason));
        }

        return Task.FromResult((true, string.Empty));
    }
}
