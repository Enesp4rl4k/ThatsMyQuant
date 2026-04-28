using System.Text.Json;
using Listener.Api.Services;
using MediatR;
using ThatsMyQuant.Contracts.Notifications;

namespace Listener.Api.Endpoints;

public sealed record SettingsDto(
    ExchangeSettingsDto Exchange,
    TelegramSettingsDto Telegram,
    RiskSettingsDto Risk,
    PaperSettingsDto Paper,
    PromotionSettingsDto Promotion);

public sealed record ExchangeSettingsDto(
    string Mode,
    bool HasApiCredentials,
    bool IsDemo);

public sealed record TelegramSettingsDto(
    bool Configured,
    bool HasChatId);

public sealed record RiskSettingsDto(
    double MaxDrawdownPercent,
    double DefaultLeverage,
    double MaxRequestedLeverage);

public sealed record PaperSettingsDto(
    string Timeframe,
    double DefaultLeverage,
    double MaxLeverage,
    int MaxConcurrentPositions,
    double MaxTotalExposurePercent,
    bool SyncWithExchange);

public sealed record PromotionSettingsDto(
    int BacktestMinTrades,
    double BacktestMinProfitFactor,
    double BacktestMinWinRate,
    int PaperMinTrades,
    double PaperMinProfitFactor,
    double PaperMinWinRate);

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/settings").WithTags("Settings");
        g.MapGet("/", GetSettings);
        g.MapGet("/writable", GetWritableSchema);
        g.MapGet("/runtime", GetRuntimeOverrides);
        g.MapPatch("/runtime", PatchRuntimeOverrides);
        g.MapGet("/exchange-credentials", GetExchangeCredentialsStatus);
        g.MapPost("/exchange-credentials", SaveExchangeCredentials);
        g.MapPost("/exchange-mode", SetExchangeMode);
        return endpoints;
    }

    /// <summary>
    /// Switches the active exchange mode by atomically rewriting
    /// <c>exchange_settings.json</c>. Mode names match what
    /// <see cref="ThatsMyQuant.Infrastructure.Persistence.SharedExchangeConfiguration.GetSelectedExchangeMode"/>
    /// recognises: <c>OKX</c>, <c>OKXSwap</c>, <c>Binance</c>, <c>BinanceFutures</c>, <c>Mock</c>.
    /// The <c>isDemo</c> flag also rewrites <c>OKX:IsDemoMode</c> so the right
    /// credential slot is picked at next startup.
    ///
    /// All three services (Listener.Api, QuantEngine.Worker, Dashboard) need
    /// to be restarted to pick up the new mode — credentials and exchange
    /// adapters are wired at startup time.
    /// </summary>
    private static async Task<IResult> SetExchangeMode(
        [Microsoft.AspNetCore.Mvc.FromBody] ExchangeModeRequestDto dto,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("SettingsEndpoints.SetExchangeMode");
        if (dto is null || string.IsNullOrWhiteSpace(dto.Mode))
            return Results.BadRequest(new { error = "mode required" });

        var allowed = new[] { "OKX", "OKXSwap", "Binance", "BinanceTestnet", "BinanceFutures", "BinanceFuturesTestnet", "Mock" };
        if (!allowed.Any(a => string.Equals(a, dto.Mode, StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = $"mode must be one of: {string.Join(", ", allowed)}" });

        try
        {
            var root = ThatsMyQuant.Infrastructure.Persistence.SharedExchangeConfiguration
                .ResolveSharedConfigRoot(AppContext.BaseDirectory);
            var path = Path.Combine(root, "exchange_settings.json");

            // Read existing JSON (preserve unknown keys), update ExchangeMode + OKX.IsDemoMode
            var dict = new Dictionary<string, object?>();
            if (File.Exists(path))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("ExchangeMode") || prop.NameEquals("OKX")) continue;
                        dict[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                    }
                }
                catch { /* corrupt → start fresh */ }
            }

            dict["ExchangeMode"] = dto.Mode;
            dict["OKX"] = new { IsDemoMode = dto.IsDemo ?? false };

            var json = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var tmp  = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);

            log.LogWarning("Exchange mode switched: {Mode} (isDemo={Demo}) → {Path}", dto.Mode, dto.IsDemo ?? false, path);
            return Results.Ok(new
            {
                mode = dto.Mode,
                isDemo = dto.IsDemo ?? false,
                path,
                note = "Saved. Restart Listener.Api + QuantEngine.Worker for the new mode to take effect.",
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SetExchangeMode failed");
            return Results.Problem(title: "Mode switch failed", detail: ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// Returns boolean flags only — NEVER the decrypted secrets. The frontend
    /// uses this to render "configured" / "not configured" labels next to each
    /// credential slot so the operator knows what's already set.
    /// </summary>
    private static IResult GetExchangeCredentialsStatus()
    {
        var (path, hasLive, hasDemo, hasBinance) = ExchangeCredentialsService.GetStatus();
        return Results.Ok(new
        {
            path,
            okxLiveConfigured = hasLive,
            okxDemoConfigured = hasDemo,
            binanceConfigured = hasBinance,
            os = Environment.OSVersion.Platform.ToString(),
            note = OperatingSystem.IsWindows()
                ? "DPAPI active. Saved credentials are user-scoped — only this Windows account can decrypt them."
                : "DPAPI requires Windows. On Linux/macOS, set credentials via env vars instead.",
        });
    }

    /// <summary>
    /// Encrypts the supplied OKX (and optionally Binance) credentials with DPAPI
    /// CurrentUser scope and writes them to <c>exchange_credentials.dpapi</c>.
    /// Restart Listener.Api + Worker for the new keys to take effect.
    /// </summary>
    private static async Task<IResult> SaveExchangeCredentials(
        [Microsoft.AspNetCore.Mvc.FromBody] ExchangeCredentialsRequestDto dto,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Results.Problem(
                title: "DPAPI not supported on this platform",
                detail: "exchange_credentials.dpapi uses Windows DPAPI. Set credentials via env vars on non-Windows hosts.",
                statusCode: 400);
        }

        if (dto is null)
            return Results.BadRequest(new { error = "body required" });

        try
        {
            var summary = await ExchangeCredentialsService.SaveAsync(dto, ct).ConfigureAwait(false);
            return Results.Ok(summary);
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Could not save credentials", detail: ex.Message, statusCode: 500);
        }
    }

    private static IResult GetWritableSchema()
    {
        var keys = WritableSettingsStore.AllowedKeys
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new
            {
                key = kv.Key,
                kind = kv.Value.Kind,
                min = kv.Value.Min,
                max = kv.Value.Max
            });
        return Results.Ok(new { keys });
    }

    private static IResult GetRuntimeOverrides(WritableSettingsStore store)
        => Results.Ok(new { path = store.Path, overrides = store.ReadAll() });

    private static async Task<IResult> PatchRuntimeOverrides(
        Dictionary<string, JsonElement> patch,
        WritableSettingsStore store,
        IMediator mediator,
        CancellationToken ct)
    {
        if (patch is null || patch.Count == 0)
        {
            return Results.BadRequest(new { error = "Empty patch." });
        }

        var result = store.ApplyPatch(patch);

        if (result.AcceptedCount > 0)
        {
            var summary = string.Join(", ", result.Accepted.Select(kv => $"{kv.Key}={kv.Value}"));
            await mediator.Publish(
                OperatorAlertNotification.Info(
                    "Settings",
                    "Runtime settings updated",
                    summary),
                ct);
        }

        return Results.Ok(new
        {
            applied = result.AcceptedCount,
            accepted = result.Accepted,
            rejected = result.Rejected
        });
    }

    private static IResult GetSettings(IConfiguration config)
    {
        var exchangeMode = config["TMQ_EXCHANGE_MODE"]
            ?? config["Exchange:Mode"]
            ?? config["SharedExchange:Mode"]
            ?? "Mock";

        var hasOkx = !string.IsNullOrWhiteSpace(config["OKX:ApiKey"]);
        var hasBinance = !string.IsNullOrWhiteSpace(config["TMQ_BINANCE_API_KEY"]);
        var isDemo = bool.TryParse(
            config["OKX:IsDemoMode"] ?? config["OKX__IsDemoMode"], out var demo) && demo;

        return Results.Ok(new SettingsDto(
            Exchange: new ExchangeSettingsDto(
                Mode: exchangeMode,
                HasApiCredentials: hasOkx || hasBinance,
                IsDemo: isDemo),
            Telegram: new TelegramSettingsDto(
                Configured: !string.IsNullOrWhiteSpace(config["Telegram:BotToken"]),
                HasChatId: !string.IsNullOrWhiteSpace(config["Telegram:ChatId"])),
            Risk: new RiskSettingsDto(
                MaxDrawdownPercent: Dbl(config, "Risk:Supervisor:MaxDrawdownPercent", 5.0),
                DefaultLeverage: Dbl(config, "Execution:Leverage:DefaultLeverage", 1.0),
                MaxRequestedLeverage: Dbl(config, "RiskGovernor:MaxRequestedLeverage", 3.0)),
            Paper: new PaperSettingsDto(
                Timeframe: config["Paper:Timeframe"] ?? "15m",
                DefaultLeverage: Dbl(config, "Paper:Leverage:DefaultLeverage", 1.0),
                MaxLeverage: Dbl(config, "Paper:Leverage:MaxLeverage", 3.0),
                MaxConcurrentPositions: Int(config, "Paper:PortfolioLimits:MaxConcurrentPositions", 2),
                MaxTotalExposurePercent: Dbl(config, "Paper:PortfolioLimits:MaxTotalExposurePercent", 25.0),
                SyncWithExchange: bool.TryParse(config["Paper:SyncWithExchange"], out var sync) && sync),
            Promotion: new PromotionSettingsDto(
                BacktestMinTrades: Int(config, "Promotion:Backtest:MinTrades", 6),
                BacktestMinProfitFactor: Dbl(config, "Promotion:Backtest:MinProfitFactor", 1.15),
                BacktestMinWinRate: Dbl(config, "Promotion:Backtest:MinWinRate", 0.42),
                PaperMinTrades: Int(config, "Promotion:Paper:MinTrades", 8),
                PaperMinProfitFactor: Dbl(config, "Promotion:Paper:MinProfitFactor", 1.20),
                PaperMinWinRate: Dbl(config, "Promotion:Paper:MinWinRate", 0.45))));
    }

    private static double Dbl(IConfiguration c, string key, double def)
        => double.TryParse(c[key], out var v) ? v : def;

    private static int Int(IConfiguration c, string key, int def)
        => int.TryParse(c[key], out var v) ? v : def;
}
