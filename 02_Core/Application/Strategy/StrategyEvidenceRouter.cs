namespace ThatsMyQuant.Application.Strategy;

/// <summary>Maps a Studio bot label to Quant Lab copy and optional UI template hints (per-strategy charts).</summary>
public static class StrategyEvidenceRouter
{
    public static string GetNarrative(string? botStrategyLabel)
    {
        if (string.IsNullOrWhiteSpace(botStrategyLabel))
        {
            return "Select a bot strategy in Strategy & AI Studio to scope Quant Lab charts, Monte Carlo input, and captions.";
        }

        var c = Compact(botStrategyLabel);
        if (c.Contains("grid", StringComparison.OrdinalIgnoreCase))
        {
            return "Grid: Monte Carlo bootstraps realized fills to stress inventory drift and path-dependent chop. Pair with ledger depth when promotion gates tighten.";
        }

        if (c.Contains("hft", StringComparison.OrdinalIgnoreCase))
        {
            return "HFT: coarse Monte Carlo on trade-level PnL; use it as a sanity band, not microstructure truth. Latency and queue position live in telemetry, not this histogram.";
        }

        if (c.Contains("mean", StringComparison.OrdinalIgnoreCase) || c.Contains("revert", StringComparison.OrdinalIgnoreCase))
        {
            return "Mean reversion: distribution width hints how often edge survives fee drag under shuffle-resample paths.";
        }

        if (c.Contains("trend", StringComparison.OrdinalIgnoreCase) || c.Contains("confluence", StringComparison.OrdinalIgnoreCase))
        {
            return "Trend / confluence: tail of simulated equity highlights fat-left scenarios when streaks break under reshuffled outcomes.";
        }

        if (c.Contains("swarm", StringComparison.OrdinalIgnoreCase))
        {
            return "Swarm: this panel still filters by the Studio label; use the strategy leaderboard for per-agent buckets when multiple agents share the book.";
        }

        return "Monte Carlo resamples closed-trade PnLs for the selected strategy to approximate path risk around your current desk capital.";
    }

    /// <summary>When the user picks Normal desk, HFT-only panels collapse to standard book context.</summary>
    public static StrategyEvidenceVisualMode ResolveVisualModeForDesk(
        StrategyEvidenceVisualMode routedFromStrategy,
        string evidenceDeskMode)
    {
        if (!evidenceDeskMode.Equals("HFT", StringComparison.OrdinalIgnoreCase)
            && routedFromStrategy == StrategyEvidenceVisualMode.Hft)
        {
            return StrategyEvidenceVisualMode.Standard;
        }

        return routedFromStrategy;
    }

    public static StrategyEvidenceVisualMode GetVisualMode(string? botStrategyLabel)
    {
        if (string.IsNullOrWhiteSpace(botStrategyLabel))
        {
            return StrategyEvidenceVisualMode.Standard;
        }

        var c = Compact(botStrategyLabel);
        if (c.Contains("grid", StringComparison.OrdinalIgnoreCase))
        {
            return StrategyEvidenceVisualMode.Grid;
        }

        if (c.Contains("hft", StringComparison.OrdinalIgnoreCase))
        {
            return StrategyEvidenceVisualMode.Hft;
        }

        return StrategyEvidenceVisualMode.Standard;
    }

    public static string GetVisualModeCaption(StrategyEvidenceVisualMode mode) => mode switch
    {
        StrategyEvidenceVisualMode.Grid => "Chart mode: grid inventory stress",
        StrategyEvidenceVisualMode.Hft => "Chart mode: HFT coarse path bands",
        _ => "Chart mode: standard bootstrap"
    };

    private static string Compact(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("·", string.Empty, StringComparison.Ordinal)
            .Trim();
}

public enum StrategyEvidenceVisualMode
{
    Standard,
    Grid,
    Hft
}
