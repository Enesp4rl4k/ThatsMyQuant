namespace ThatsMyQuant.Contracts.Options;

public sealed class MomentumOptions
{
    public int RsiPeriod { get; set; } = 14;
    public double RsiBullThreshold { get; set; } = 60.0;
    public double RsiBearThreshold { get; set; } = 40.0;
    public int AdxPeriod { get; set; } = 14;
    public double AdxTrendThreshold { get; set; } = 20.0;
    public double AdxStrongThreshold { get; set; } = 25.0;
    public int EmaFastPeriod { get; set; } = 9;
    public int EmaSlowPeriod { get; set; } = 21;
    public int MinCandles { get; set; } = 30;
    public int SwarmWeight { get; set; } = 20;
    public int MultiTfAggFactor { get; set; } = 4;
    public double MultiTfRsiMidpoint { get; set; } = 50.0;
    public int BaseConfidencePerFactor { get; set; } = 6;
    public int VolumeExpansionBonus { get; set; } = 4;
    public int StrongTrendBonus { get; set; } = 2;
    public int VwapAgreementBonus { get; set; } = 2;
    public int VwapDisagreementPenalty { get; set; } = 4;
    public double VolumeExpansionMultiplier { get; set; } = 1.1;
}

public sealed class BreakoutOptions
{
    public int LookbackBars { get; set; } = 50;
    public int MinConsolidationBars { get; set; } = 10;
    public int VolumeAvgBars { get; set; } = 20;
    public double VolumeSurgeMultiple { get; set; } = 1.5;
    public double FibBullLevel { get; set; } = 0.786;
    public double FibBearLevel { get; set; } = 0.214;
    public int MaxSwingStaleBars { get; set; } = 40;
    public double RangeThresholdPercent { get; set; } = 0.03;
    public int RetestLookbackBars { get; set; } = 5;
    public double RetestProximityPercent { get; set; } = 0.005;
    public int VolumeProfileBins { get; set; } = 10;
    public double ThinAirThreshold { get; set; } = 0.5;
    public int SwarmWeight { get; set; } = 25;
    public int BaseConfidencePerFactor { get; set; } = 6;
    public int LongBaseDurationBonus { get; set; } = 4;
    public int MediumBaseDurationBonus { get; set; } = 2;
    public int ThinAirBonus { get; set; } = 2;
    public double TightConsolidationQuality { get; set; } = 0.6;
    public int TightConsolidationBonus { get; set; } = 2;
    public int MediumBase { get; set; } = 20;
    public int LongBase { get; set; } = 35;
}

public sealed class ScalpingOptions
{
    public double StrongOfiThreshold { get; set; } = 0.6;
    public double MinOfiThreshold { get; set; } = 0.5;
    public double VeryStrongOfiThreshold { get; set; } = 0.8;
    public double MaxSpreadBps { get; set; } = 10.0;
    public double TightSpreadBps { get; set; } = 5.0;
    public double MinFillProbability { get; set; } = 0.7;
    public double MaxVwapDeviation { get; set; } = 1.5;
    public int SwarmWeight { get; set; } = 15;
    public int BaseConfidencePerFactor { get; set; } = 5;
    public int VeryStrongOfiBonus { get; set; } = 3;
    public int TightSpreadBonus { get; set; } = 2;
}

public sealed class StatArbOptions
{
    public int LookbackBars { get; set; } = 50;
    public double EntryZScore { get; set; } = 2.5;
    public double MinCoefficientOfVariation { get; set; } = 0.005;
    public double FastHalfLife { get; set; } = 5.0;
    public double MaxHalfLife { get; set; } = 15.0;
    public double AdfCriticalValue { get; set; } = -2.86;
    public double AdfBonusOffset { get; set; } = 1.0;
    public double ZScoreMagnitudeCap { get; set; } = 2.0;
    public double ZScoreBonusMultiplier { get; set; } = 2.5;
    public double VwapAgreementThreshold { get; set; } = 0.5;
    public int SwarmWeight { get; set; } = 30;
    public int BaseConfidencePerFactor { get; set; } = 5;
    public int FastHalfLifeBonus { get; set; } = 3;
    public int StrongStationarityBonus { get; set; } = 2;
    public int VwapAgreementBonus { get; set; } = 2;
}
