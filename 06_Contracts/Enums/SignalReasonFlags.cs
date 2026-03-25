using System;

namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Bitwise enum representing the combinatorial factors invoking a trading signal.
/// Bypasses the Garbage Collector penalty of string += tracing.
/// </summary>
[Flags]
public enum SignalReasonFlags
{
    None = 0,
    OFIFrontRunBuy = 1 << 0,
    OFIFrontRunSell = 1 << 1,
    PriceActionExhaustion = 1 << 2,
    VolumeAnomaly = 1 << 3,
    ArbitrageOpportunity = 1 << 4,
    Confluence = 1 << 5,
    SentimentVeto = 1 << 6,
    RiskVeto = 1 << 7,
    MachineLearningVeto = 1 << 8,
    SystemError = 1 << 9,
    AiVeto = 1 << 10,
    KellyVeto = 1 << 11,
    PortfolioOptimiserVeto = 1 << 12,
    CorrelationGuardVeto = 1 << 13,
    RegimeSwitchingVeto = 1 << 14,
    BanditVeto = 1 << 15,
    MetaLearningVeto = 1 << 16
}

