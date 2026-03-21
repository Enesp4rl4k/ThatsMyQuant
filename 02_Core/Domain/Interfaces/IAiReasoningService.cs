using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Domain.Interfaces;

public interface IAiReasoningService
{
    Task<bool> ShouldExecuteAsync(Signal signal);
    Task<string> GetReasoningAsync(Signal signal);
}
