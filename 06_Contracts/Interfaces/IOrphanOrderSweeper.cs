namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Implemented by venue services that can cancel resting orders this bot placed in an earlier run.
/// Orders placed by hand on the venue are left alone.
/// </summary>
public interface IOrphanOrderSweeper
{
    /// <summary>Cancels the bot's own resting orders and returns how many were cancelled.</summary>
    Task<int> CancelOwnRestingOrdersAsync(CancellationToken cancellationToken = default);
}
