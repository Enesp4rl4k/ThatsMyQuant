namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// PBO segment partitioning strategy.
/// Contiguous preserves regime clusters and exposes regime-overfit params honestly.
/// Interleaved stride-samples across regimes and can deflate PBO.
/// </summary>
public enum PboPartitionMode
{
    Contiguous = 0,
    Interleaved = 1
}
