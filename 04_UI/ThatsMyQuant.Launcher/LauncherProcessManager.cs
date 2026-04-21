using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace ThatsMyQuant.Launcher;

public sealed class LauncherProcessManager : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string? _bundleRoot;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, ManagedProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public LauncherProcessManager(string workspaceRoot, string? bundleRoot)
    {
        _workspaceRoot = workspaceRoot;
        _bundleRoot = bundleRoot;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public event Action<string>? OutputReceived;

    public IReadOnlyCollection<string> RunningKeys
    {
        get
        {
            lock (_sync)
            {
                return _processes.Keys.ToArray();
            }
        }
    }

    public bool IsRunning(string displayName)
    {
        lock (_sync)
        {
            return _processes.TryGetValue(displayName, out var process) && !process.Process.HasExited;
        }
    }

    public async Task StartAsync(LauncherServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        if (IsRunning(definition.DisplayName))
        {
            return;
        }

        var startInfo = BuildStartInfo(definition);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                OutputReceived?.Invoke($"[{definition.DisplayName}] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                OutputReceived?.Invoke($"[{definition.DisplayName} ERR] {args.Data}");
            }
        };
        process.Exited += (_, _) =>
        {
            OutputReceived?.Invoke($"[{definition.DisplayName}] process exited with code {process.ExitCode}.");
            lock (_sync)
            {
                _processes.Remove(definition.DisplayName);
            }
            process.Dispose();
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {definition.DisplayName}.");
        }

        if (!definition.LaunchWindow)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        lock (_sync)
        {
            _processes[definition.DisplayName] = new ManagedProcess(definition, process);
        }
        OutputReceived?.Invoke($"[{definition.DisplayName}] started.");

        if (!string.IsNullOrWhiteSpace(definition.HealthUrl))
        {
            await WaitForHealthyAsync(definition.HealthUrl!, cancellationToken);
        }
    }

    public async Task StopAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ManagedProcess? managed;
        lock (_sync)
        {
            if (!_processes.TryGetValue(displayName, out managed))
            {
                return;
            }
        }

        var process = managed.Process;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    if (!process.CloseMainWindow())
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    else if (!await process.WaitForExitAsync(TimeSpan.FromSeconds(5), cancellationToken))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _processes.Remove(displayName);
            }
            process.Dispose();
            OutputReceived?.Invoke($"[{displayName}] stopped.");
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        string[] running;
        lock (_sync)
        {
            running = _processes.Keys.ToArray();
        }

        foreach (var displayName in running.Reverse())
        {
            await StopAsync(displayName, cancellationToken);
        }
    }

    public async Task<bool> CheckHealthAsync(string healthUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(healthUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private ProcessStartInfo BuildStartInfo(LauncherServiceDefinition definition)
    {
        var publishedPath = ResolvePublishedExecutable(definition);
        var requiresCustomEnvironment = definition.Environment is { Count: > 0 };
        ProcessStartInfo startInfo;
        if (publishedPath is not null)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = publishedPath,
                WorkingDirectory = Path.GetDirectoryName(publishedPath)!,
                UseShellExecute = definition.LaunchWindow && !requiresCustomEnvironment
            };
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{Path.Combine(_workspaceRoot, definition.ProjectPath)}\"",
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = definition.LaunchWindow && !requiresCustomEnvironment
            };
        }

        if (!startInfo.UseShellExecute)
        {
            startInfo.RedirectStandardOutput = !definition.LaunchWindow;
            startInfo.RedirectStandardError = !definition.LaunchWindow;
            if (startInfo.RedirectStandardOutput)
            {
                startInfo.StandardOutputEncoding = Encoding.UTF8;
            }

            if (startInfo.RedirectStandardError)
            {
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }

            startInfo.CreateNoWindow = !definition.LaunchWindow;
        }

        if (definition.Environment is not null)
        {
            foreach (var pair in definition.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private string? ResolvePublishedExecutable(LauncherServiceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(_bundleRoot))
        {
            return null;
        }

        var path = Path.Combine(_bundleRoot, definition.BundleDirectory, definition.ExecutableName);
        return File.Exists(path) ? path : null;
    }

    private async Task WaitForHealthyAsync(string healthUrl, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await CheckHealthAsync(healthUrl, cancellationToken))
            {
                return;
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {healthUrl} to become healthy.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        ManagedProcess[] running;
        lock (_sync)
        {
            running = _processes.Values.ToArray();
            _processes.Clear();
        }

        foreach (var process in running)
        {
            process.Process.Dispose();
        }
    }

    private sealed record ManagedProcess(LauncherServiceDefinition Definition, Process Process);
}

internal static class ProcessExtensions
{
    public static async Task<bool> WaitForExitAsync(this Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }
}
