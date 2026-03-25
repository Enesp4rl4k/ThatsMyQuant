using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

public record RiskGuardResult
{
    public string GuardId { get; init; } = string.Empty;
    public bool Approved { get; init; }
    public decimal SizeMultiplier { get; init; } = 1m;
    public SignalReasonFlags VetoFlag { get; init; } = SignalReasonFlags.None;
    public string Reason { get; init; } = string.Empty;

    public static RiskGuardResult Pass(string guardId, decimal sizeMultiplier = 1m)
        => new() { GuardId = guardId, Approved = true, SizeMultiplier = sizeMultiplier };

    public static RiskGuardResult Block(string guardId, SignalReasonFlags flag, string reason)
        => new() { GuardId = guardId, Approved = false, VetoFlag = flag, Reason = reason, SizeMultiplier = 0m };
}
