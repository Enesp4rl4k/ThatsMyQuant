using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ThatsMyQuant.Tests.TestInfrastructure;

internal static class TestModelCache
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> TrainedModels = new();

    public static void EnsureModel(string targetPath, string cacheKey, Action<string> trainModel)
    {
        var sourcePath = TrainedModels.GetOrAdd(
            cacheKey,
            key => new Lazy<string>(() => BuildModelOnce(key, trainModel), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    private static string BuildModelOnce(string cacheKey, Action<string> trainModel)
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "tmq-test-model-cache");
        Directory.CreateDirectory(cacheDirectory);

        var cacheFileName = $"{Hash(cacheKey)}.zip";
        var cachePath = Path.Combine(cacheDirectory, cacheFileName);
        if (!File.Exists(cachePath))
        {
            trainModel(cachePath);
        }

        return cachePath;
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
