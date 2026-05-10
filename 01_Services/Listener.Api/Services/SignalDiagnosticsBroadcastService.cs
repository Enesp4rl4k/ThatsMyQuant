using Microsoft.AspNetCore.SignalR;
using Listener.Api.Hubs;
using ThatsMyQuant.Application.Diagnostics;

namespace Listener.Api.Services;

/// <summary>
/// Polls <see cref="ISignalDiagnosticsRegistry"/> every 3 seconds and broadcasts the
/// full panel snapshot to all subscribers of <see cref="SignalDiagnosticsHub"/> via
/// the <c>SignalDiagnosticsUpdate</c> event.
/// </summary>
/// <remarks>
/// <para><b>Why server-side polling instead of executor-push.</b> Executors publish
/// snapshots at their own (varying) cadence — funding ticks every 30 min, pairs every
/// 5 min, EdgeLab hourly. Pushing each individual update would create bursty SignalR
/// traffic and force clients to maintain per-row state. The 3s server-side broadcast
/// gives clients a uniform, debounced full snapshot they can trivially render.</para>
///
/// <para><b>Wire format.</b> Each broadcast carries an array of
/// <see cref="SignalDiagnosticRowDto"/> — a flattened, JSON-friendly projection of
/// <see cref="SignalDiagnosticSnapshot"/> with the enum serialised as string. The
/// React side just <c>setQueryData(['signal-diagnostics'], rows)</c> on receipt.</para>
///
/// <para><b>No-op when no subscribers.</b> The hub fan-out is cheap when the group is
/// empty, but we still skip the registry read + DTO mapping in that case to keep the
/// worker quiet during operator inactivity.</para>
/// </remarks>
public sealed class SignalDiagnosticsBroadcastService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly ISignalDiagnosticsRegistry _registry;
    private readonly IHubContext<SignalDiagnosticsHub> _hub;
    private readonly ILogger<SignalDiagnosticsBroadcastService> _logger;

    public SignalDiagnosticsBroadcastService(
        ISignalDiagnosticsRegistry registry,
        IHubContext<SignalDiagnosticsHub> hub,
        ILogger<SignalDiagnosticsBroadcastService> logger)
    {
        _registry = registry;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so executors have time to publish their first snapshots.
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "SignalDiagnosticsBroadcastService starting. Tick interval = {Interval}s.",
            TickInterval.TotalSeconds);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            do
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SignalDiagnostics broadcast tick failed (non-fatal).");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var snapshots = _registry.GetAll();
        if (snapshots.Count == 0) return;

        var rows = new SignalDiagnosticRowDto[snapshots.Count];
        for (int i = 0; i < snapshots.Count; i++)
            rows[i] = SignalDiagnosticRowDto.From(snapshots[i]);

        // Group-targeted send: only clients that called JoinSignalDiagnostics receive this.
        await _hub.Clients.Group("signal-diagnostics")
            .SendAsync("SignalDiagnosticsUpdate", rows, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Wire DTO for the React Diagnostics page. Mirrors
/// <see cref="SignalDiagnosticSnapshot"/> but serialises the
/// <see cref="SignalDecision"/> enum as a string so the frontend doesn't need
/// to maintain a parallel numeric mapping.
/// </summary>
public sealed record SignalDiagnosticRowDto(
    DateTime UtcTimestamp,
    string  Tier,
    string  StrategyTag,
    string  Key,
    decimal CurrentValue,
    decimal EntryThreshold,
    decimal? ExitThreshold,
    decimal? StopThreshold,
    decimal DistanceToTriggerPct,
    string  Decision,
    string? SkipOrSuppressReason,
    string? RegimeContext,
    bool    HasOpenPosition,
    decimal? EstimatedNotionalIfFired)
{
    public static SignalDiagnosticRowDto From(SignalDiagnosticSnapshot s) => new(
        UtcTimestamp:             s.UtcTimestamp,
        Tier:                     s.Tier,
        StrategyTag:              s.StrategyTag,
        Key:                      s.Key,
        CurrentValue:             s.CurrentValue,
        EntryThreshold:           s.EntryThreshold,
        ExitThreshold:            s.ExitThreshold,
        StopThreshold:            s.StopThreshold,
        DistanceToTriggerPct:     s.DistanceToTriggerPct,
        Decision:                 s.Decision.ToString(),
        SkipOrSuppressReason:     s.SkipOrSuppressReason,
        RegimeContext:            s.RegimeContext,
        HasOpenPosition:          s.HasOpenPosition,
        EstimatedNotionalIfFired: s.EstimatedNotionalIfFired);
}
