namespace ThatsMyQuant.Launcher;

public sealed record LauncherServiceDefinition(
    string DisplayName,
    string BundleDirectory,
    string ExecutableName,
    string ProjectPath,
    string? HealthUrl = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool LaunchWindow = false);
