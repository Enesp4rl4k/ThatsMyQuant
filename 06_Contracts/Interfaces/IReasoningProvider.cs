using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IReasoningProvider
{
    Task<bool> ShouldExecuteAsync(Signal signal);
    Task<string> GetReasoningAsync(Signal signal);
}
