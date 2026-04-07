namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Abstraction for persisting and loading ML model artifacts.
/// Keeps the Core/Application layer free from direct file I/O.
/// </summary>
public interface IModelArtifactStore
{
    /// <summary>Returns true when an artifact for <paramref name="modelName"/> already exists.</summary>
    Task<bool> ExistsAsync(string modelName, CancellationToken ct = default);

    /// <summary>Loads the serialised artifact content for <paramref name="modelName"/>.</summary>
    Task<string> LoadAsync(string modelName, CancellationToken ct = default);

    /// <summary>Persists <paramref name="content"/> as the artifact for <paramref name="modelName"/>.</summary>
    Task SaveAsync(string modelName, string content, CancellationToken ct = default);

    /// <summary>Deletes the artifact for <paramref name="modelName"/> if it exists.</summary>
    Task DeleteAsync(string modelName, CancellationToken ct = default);

    /// <summary>Returns an opaque path or identifier for the artifact (used for logging/diagnostics).</summary>
    string GetArtifactPath(string modelName);
}
