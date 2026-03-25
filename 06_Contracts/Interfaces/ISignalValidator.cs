using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Defines a modular validator for incoming trading signals.
/// Allows for composable validation chains (e.g., Volume, Price, Cooldown).
/// </summary>
public interface ISignalValidator
{
    Task<(bool IsValid, string FailureReason)> ValidateAsync(Signal signal);
    
    /// <summary>
    /// Execution order in the validation chain. Lower numbers run first.
    /// </summary>
    int Order { get; }
}
