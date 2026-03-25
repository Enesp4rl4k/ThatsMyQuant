using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Defines the capabilities for an agent that can participate in a "Focused Backtest" session.
/// Allows for sequential data processing and state resets.
/// </summary>
public interface IFocusedAgent
{
    /// <summary>
    /// Resets the agent's internal state (buffers, statistics, etc.) for a specific symbol.
    /// Used when switching focus during a backtest.
    /// </summary>
    Task ResetAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Processes a single step (candle) in a simulation.
    /// This allows for the "streaming" effect in the UI.
    /// </summary>
    Task ProcessStepAsync(Candle candle, CancellationToken ct = default);
}
