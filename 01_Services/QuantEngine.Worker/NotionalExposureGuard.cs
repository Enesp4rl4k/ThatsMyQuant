using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Cross-tier total-notional cap. Before opening any new position, executors call
/// <see cref="TryReserve"/> with the proposed notional. If summing the new notional
/// + currently-open notional would exceed <c>Hybrid:MaxTotalNotionalRatio</c> × basis,
/// the reservation FAILS and the executor must skip the entry.
///
/// Critical for live: prevents cash overrun when many tiers fire simultaneously
/// (we observed 6 trades opening in 1 second after the runner bug fix).
///
/// On close, executors call <see cref="Release"/> to free the reservation.
/// Reservations are keyed by (tier, symbol|pair) so each open position can be tracked
/// and released independently.
/// </summary>
public sealed class NotionalExposureGuard
{
    private readonly ILogger<NotionalExposureGuard> _logger;
    private readonly decimal _maxNotionalRatio;
    private readonly ConcurrentDictionary<string, decimal> _reservations = new();

    public NotionalExposureGuard(ILogger<NotionalExposureGuard> logger, IConfiguration cfg)
    {
        _logger = logger;
        var raw = cfg["Hybrid:MaxTotalNotionalRatio"]
            ?? cfg["Paper:PortfolioLimits:MaxTotalNotionalRatio"];
        _maxNotionalRatio = decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0m ? v : 0.40m;
        _logger.LogInformation("NotionalExposureGuard initialised. MaxRatio={Ratio:P0} of basis.", _maxNotionalRatio);
    }

    /// <summary>Cap as fraction of basis (e.g. 0.40 = 40%).</summary>
    public decimal MaxRatio => _maxNotionalRatio;

    /// <summary>Sum of all currently-reserved notional across tiers.</summary>
    public decimal CurrentReservedNotional => _reservations.Values.Sum();

    /// <summary>
    /// Atomically check + reserve. Returns true if the reservation was made (executor
    /// MUST proceed with the open + later call <see cref="Release"/>). Returns false
    /// if accepting would breach the cap (executor MUST abort the open).
    /// </summary>
    public bool TryReserve(string tier, string positionKey, decimal proposedNotional, decimal basis)
    {
        if (proposedNotional <= 0m) return true; // nothing to reserve
        if (basis <= 0m)
        {
            _logger.LogWarning("NotionalExposureGuard: basis={Basis} ≤ 0 — refusing reservation for {Tier}:{Key}", basis, tier, positionKey);
            return false;
        }

        var key = $"{tier}|{positionKey}";
        var capAbsolute = basis * _maxNotionalRatio;

        // Use lock-free try-add then validate. If validation fails, undo.
        if (!_reservations.TryAdd(key, proposedNotional))
        {
            // Already reserved — replace with new value (idempotent re-attempt).
            _reservations[key] = proposedNotional;
        }

        var totalAfter = CurrentReservedNotional;
        if (totalAfter > capAbsolute)
        {
            _reservations.TryRemove(key, out _);
            _logger.LogWarning(
                "NotionalExposureGuard REJECTED {Tier}:{Key}: proposed={Proposed:N2}, current reserved={Reserved:N2}, would-be={Total:N2} > cap={Cap:N2} ({Pct:P0} of ${Basis:N0})",
                tier, positionKey, proposedNotional, totalAfter - proposedNotional, totalAfter, capAbsolute, _maxNotionalRatio, basis);
            return false;
        }
        _logger.LogDebug("NotionalExposureGuard OK {Tier}:{Key}: reserved=${R:N2}, total=${T:N2} of ${Cap:N2}",
            tier, positionKey, proposedNotional, totalAfter, capAbsolute);
        return true;
    }

    /// <summary>Release a previously-made reservation (called on position close).</summary>
    public void Release(string tier, string positionKey)
    {
        var key = $"{tier}|{positionKey}";
        if (_reservations.TryRemove(key, out var freed))
        {
            _logger.LogDebug("NotionalExposureGuard released {Tier}:{Key} (${Freed:N2}). Remaining total=${T:N2}",
                tier, positionKey, freed, CurrentReservedNotional);
        }
    }
}
