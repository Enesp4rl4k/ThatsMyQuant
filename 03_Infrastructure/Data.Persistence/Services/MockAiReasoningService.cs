using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class MockAiReasoningService : IReasoningProvider
{
    public Task<bool> ShouldExecuteAsync(Signal signal)
    {
        // Mock logic: Approve signals with price ending in .00
        return Task.FromResult(signal.Price % 1 == 0);
    }

    public Task<string> GetReasoningAsync(Signal signal)
    {
        return Task.FromResult(signal.Price % 1 == 0 
            ? "Price is a round number, looks stable for entry." 
            : "Price is not a round number, risk is too high.");
    }
}
