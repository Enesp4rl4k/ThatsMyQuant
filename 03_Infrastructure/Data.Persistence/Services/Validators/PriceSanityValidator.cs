using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Validators;

public class PriceSanityValidator : ISignalValidator
{
    private readonly ILogger<PriceSanityValidator> _logger;

    public PriceSanityValidator(ILogger<PriceSanityValidator> logger)
    {
        _logger = logger;
    }

    public int Order => 5; // Run early

    public Task<(bool IsValid, string FailureReason)> ValidateAsync(Signal signal)
    {
        if (signal.Price <= 0)
        {
            var reason = $"Invalid price: ${signal.Price}. Price must be greater than zero.";
            _logger.LogWarning("Validation Failed: {Reason}", reason);
            return Task.FromResult((false, reason));
        }

        return Task.FromResult((true, string.Empty));
    }
}
