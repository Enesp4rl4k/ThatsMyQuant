using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Alerts;

/// <summary>
/// Read-only Telegram command bot. Long-polls <c>getUpdates</c> and replies to
/// /status, /pnl, /positions, /regime, /help issued from the authorized chat(s).
/// Disabled by default — opt in via <c>Telegram:EnableReadBot=true</c>.
/// </summary>
public sealed class TelegramReadBotHostedService : BackgroundService
{
    private readonly ILogger<TelegramReadBotHostedService> _logger;
    private readonly IOptions<TelegramOptions> _options;
    private readonly IServiceProvider _services;
    private readonly HttpClient _http;
    private long _updateOffset;

    public TelegramReadBotHostedService(
        ILogger<TelegramReadBotHostedService> logger,
        IOptions<TelegramOptions> options,
        IServiceProvider services,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _options = options;
        _services = services;
        _http = httpFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(40);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.EnableReadBot || string.IsNullOrWhiteSpace(opts.BotToken))
        {
            _logger.LogInformation("TelegramReadBotHostedService disabled (EnableReadBot={Enabled}, token set={HasToken}).",
                opts.EnableReadBot, !string.IsNullOrWhiteSpace(opts.BotToken));
            return;
        }

        var allowed = BuildAllowedSet(opts);
        _logger.LogInformation("Telegram read-bot started. Allowed chat IDs: {Ids}", string.Join(",", allowed));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await FetchUpdatesAsync(opts.BotToken, stoppingToken).ConfigureAwait(false);
                foreach (var update in updates)
                {
                    await HandleUpdateAsync(opts.BotToken, update, allowed, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram read-bot poll failed; backing off 10s.");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static HashSet<string> BuildAllowedSet(TelegramOptions opts)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(opts.ChatId)) set.Add(opts.ChatId.Trim());
        if (!string.IsNullOrWhiteSpace(opts.AllowedChatIds))
        {
            foreach (var id in opts.AllowedChatIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(id);
        }
        return set;
    }

    private async Task<List<JsonElement>> FetchUpdatesAsync(string token, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{token}/getUpdates?offset={_updateOffset}&timeout=30&allowed_updates=[\"message\"]";
        var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return new List<JsonElement>();
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var list = new List<JsonElement>();
        if (payload.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
            payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in result.EnumerateArray())
            {
                if (u.TryGetProperty("update_id", out var idProp))
                    _updateOffset = Math.Max(_updateOffset, idProp.GetInt64() + 1);
                list.Add(u.Clone());
            }
        }
        return list;
    }

    private async Task HandleUpdateAsync(string token, JsonElement update, HashSet<string> allowed, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatIdEl)) return;
        var chatId = chatIdEl.GetRawText();
        if (allowed.Count > 0 && !allowed.Contains(chatId))
        {
            _logger.LogInformation("Ignoring Telegram message from unauthorized chat {ChatId}.", chatId);
            return;
        }
        if (!msg.TryGetProperty("text", out var textEl)) return;
        var text = (textEl.GetString() ?? "").Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith('/')) return;

        var cmd = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        var reply = cmd switch
        {
            "/help" => BuildHelpReply(),
            "/status" => BuildStatusReply(),
            "/pnl" => BuildPnlReply(),
            "/positions" => BuildPositionsReply(),
            "/regime" => await BuildRegimeReplyAsync(text, ct).ConfigureAwait(false),
            _ => $"Unknown command `{cmd}`. Try /help."
        };

        await SendMessageAsync(token, chatId, reply, ct).ConfigureAwait(false);
    }

    private static string BuildHelpReply() =>
        "ThatsMyQuant read-only bot\n" +
        "/status — service readiness + circuit breaker\n" +
        "/pnl — today's PnL summary\n" +
        "/positions — current open positions\n" +
        "/regime [SYMBOL] [TF] — current regime (default BTCUSDT 1h)\n" +
        "/help — this list";

    private string BuildStatusReply()
    {
        using var scope = _services.CreateScope();
        var runtime = scope.ServiceProvider.GetService<ServiceRuntimeState>();
        var cb = scope.ServiceProvider.GetService<CircuitBreakerService>()?.GetStatus();
        var tel = scope.ServiceProvider.GetService<OperationalTelemetry>()?.GetSnapshot();

        var lines = new List<string> { "📊 STATUS" };
        if (runtime is not null)
        {
            var snap = runtime.GetSnapshot();
            lines.Add($"Service: {snap.ServiceName}");
            lines.Add($"Ready: {snap.IsReady}");
            lines.Add($"Started: {snap.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        }
        if (cb is not null)
        {
            lines.Add($"CircuitBreaker: {(cb.IsHalted ? "🚨 HALTED" : "✓ clear")}");
            lines.Add($"DD: {cb.CurrentDrawdownPercent:F2}% · consec errors: {cb.ConsecutiveErrors}");
        }
        if (tel is not null)
        {
            lines.Add($"Trades ok/fail: {tel.TradesSucceeded}/{tel.TradesFailed}");
            lines.Add($"Exec latency avg: {tel.AverageExecutionLatencyMs:F0}ms");
        }
        return string.Join('\n', lines);
    }

    private string BuildPnlReply()
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetService<ITradeRepository>();
        if (repo is null) return "PnL unavailable (trade repo not registered).";
        try
        {
            var now = DateTime.UtcNow;
            var trades = repo.GetAllAsync(500).GetAwaiter().GetResult();
            decimal Range(DateTime cutoff) => trades
                .Where(t => t.ExitTime.HasValue && t.ExitTime!.Value >= cutoff && t.EntryPrice > 0m)
                .Sum(t => TradePnl(t));
            var h1 = Range(now.AddHours(-1));
            var d1 = Range(now.AddDays(-1));
            var w1 = Range(now.AddDays(-7));
            return $"💰 PnL\n1h: {h1:+0.00;-0.00;0.00}\n1d: {d1:+0.00;-0.00;0.00}\n1w: {w1:+0.00;-0.00;0.00}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PnL command failed.");
            return $"PnL lookup failed: {ex.Message}";
        }
    }

    private static decimal TradePnl(Contracts.Models.Trade t)
    {
        var exit = t.AverageExitPrice > 0m ? t.AverageExitPrice : t.ExitPrice;
        if (t.EntryPrice <= 0m || exit <= 0m) return 0m;
        var direction = t.Side == Contracts.Enums.TradeSide.Buy ? 1m : -1m;
        return (exit - t.EntryPrice) * t.Quantity * direction - t.TotalFeesPaid;
    }

    private string BuildPositionsReply()
    {
        using var scope = _services.CreateScope();
        var pm = scope.ServiceProvider.GetService<IPortfolioManager>();
        if (pm is null) return "Portfolio manager unavailable.";
        var positions = pm.GetAllPositions();
        if (positions.Count == 0) return "📋 No open positions.";
        var lines = new List<string> { $"📋 OPEN POSITIONS ({positions.Count})" };
        foreach (var (sym, p) in positions)
        {
            var side = p.IsLong ? "LONG" : "SHORT";
            lines.Add($"• {sym} {side} qty={p.Quantity:F4} @ {p.EntryPrice:F4}");
        }
        return string.Join('\n', lines);
    }

    private async Task<string> BuildRegimeReplyAsync(string text, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sym = parts.Length > 1 ? parts[1].ToUpperInvariant() : "BTCUSDT";
        var tf = parts.Length > 2 ? parts[2] : "1h";

        using var scope = _services.CreateScope();
        var md = scope.ServiceProvider.GetService<IMarketDataService>();
        if (md is null) return "Market data unavailable.";
        try
        {
            var candles = await md.GetCandlesAsync(sym, 120, "Crypto", tf).ConfigureAwait(false);
            if (candles is null || candles.Count < 60) return $"Not enough candles for {sym} {tf}.";
            var res = MarketRegimeDetector.Detect(candles, lookback: 20, lagBars: 0, timeframe: tf);
            return $"🎯 {sym} {tf} regime: {res.Regime}\n{res.Description}";
        }
        catch (Exception ex)
        {
            return $"Regime lookup failed: {ex.Message}";
        }
    }

    private async Task SendMessageAsync(string token, string chatId, string text, CancellationToken ct)
    {
        try
        {
            var payload = new { chat_id = chatId.Trim('"'), text, parse_mode = (string?)null };
            var resp = await _http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", payload, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Telegram sendMessage failed: {Status} {Err}", resp.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage threw.");
        }
    }
}
