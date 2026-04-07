using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// File-system backed implementation of <see cref="IModelArtifactStore"/>.
/// Stores each model artifact as a JSON file under a configurable root directory.
/// </summary>
public sealed class FileSystemModelArtifactStore : IModelArtifactStore
{
    private readonly string _rootDirectory;

    public FileSystemModelArtifactStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant",
                "ml-models");

        Directory.CreateDirectory(_rootDirectory);
    }

    public Task<bool> ExistsAsync(string modelName, CancellationToken ct = default)
    {
        var path = GetArtifactPath(modelName);
        return Task.FromResult(File.Exists(path));
    }

    public async Task<string> LoadAsync(string modelName, CancellationToken ct = default)
    {
        var path = GetArtifactPath(modelName);
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task SaveAsync(string modelName, string content, CancellationToken ct = default)
    {
        var path = GetArtifactPath(modelName);
        await File.WriteAllTextAsync(path, content, ct);
    }

    public Task DeleteAsync(string modelName, CancellationToken ct = default)
    {
        var path = GetArtifactPath(modelName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public string GetArtifactPath(string modelName)
        => Path.Combine(_rootDirectory, $"{modelName}.json");
}
