using System.Text.Json;
using ThatsMyQuant.Contracts.Interfaces;

namespace Listener.Api.Endpoints;

/// <summary>
/// Bridge endpoints for reading the **paper-trading runtime state** that lives
/// in JSON files (`funding_long_state.json`, `funding_short_state.json`,
/// `pairs_long_state.json`, `pairs_short_state.json`) maintained by the
/// hybrid executors in QuantEngine.Worker.
///
/// These positions DON'T currently land in the SQLite Trade table — that
/// architectural gap means /api/positions and /api/portfolio show empty even
/// when the bot is actively trading.  This endpoint is the temporary surface
/// the dashboard reads from to display live activity.
///
/// Long-term fix: hybrid executors should write to ITradeRepository on
/// entry/exit so /api/positions works directly.  Until then, this endpoint
/// reads the canonical state files and unifies them into a single list.
///
/// Also: `GET /api/paper/exchange-positions` calls IExchangeService.GetAccountStatusAsync
/// to fetch REAL OKX demo account positions from the exchange (separate from
/// the bot's internal in-memory state).
/// </summary>
public static class PaperStateEndpoints
{
    public static IEndpointRouteBuilder MapPaperStateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/paper").WithTags("Paper");
        g.MapGet("/state-positions",    GetStatePositions);
        g.MapGet("/exchange-positions", GetExchangePositions);
        g.MapGet("/portfolio",          GetPortfolioSnapshotAsync);
        g.MapGet("/closed-trades",      GetClosedTrades);
        return endpoints;
    }

    /// <summary>
    /// Closed trades from the worker's paper_portfolio.json snapshot. Lets Trade Journal
    /// + Quant Lab show real history even though hybrid executors don't write to the DB
    /// Trade table. Returns an empty array (not 500) when the snapshot doesn't exist yet.
    /// </summary>
    private static IResult GetClosedTrades()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant",
            "paper_portfolio.json");
        if (!File.Exists(path))
        {
            return Results.Ok(new ClosedTradesResponseDto(Array.Empty<ClosedTradeDto>(), 0, "snapshot-pending"));
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("ClosedPositions", out var cp) || cp.ValueKind != JsonValueKind.Array)
                return Results.Ok(new ClosedTradesResponseDto(Array.Empty<ClosedTradeDto>(), 0, "no-closed-positions"));

            var rows = new List<ClosedTradeDto>(cp.GetArrayLength());
            foreach (var p in cp.EnumerateArray())
            {
                rows.Add(new ClosedTradeDto(
                    Symbol:        TryGetString(p, "Symbol") ?? "",
                    Side:          (TryGetInt(p, "Side") ?? 0) == 0 ? "LONG" : "SHORT",
                    Quantity:      TryGetDecimal(p, "Quantity") ?? 0m,
                    EntryPrice:    TryGetDecimal(p, "EntryPrice") ?? 0m,
                    ExitPrice:     TryGetDecimal(p, "CurrentPrice") ?? 0m,
                    RealizedPnL:   TryGetDecimal(p, "RealizedPnL") ?? 0m,
                    EntryTimeUtc:  ParseDateTime(TryGetString(p, "EntryTime")),
                    ExitTimeUtc:   ParseDateTime(TryGetString(p, "ExitTime")),
                    StrategyTag:   TryGetString(p, "StrategyTag") ?? "",
                    ExecutionMode: TryGetString(p, "ExecutionMode") ?? "PAPER",
                    ExitReason:    TryGetString(p, "ExitReason") ?? ""));
            }
            // Newest first for journal UX
            rows.Sort((a, b) => b.ExitTimeUtc.CompareTo(a.ExitTimeUtc));
            return Results.Ok(new ClosedTradesResponseDto(rows.ToArray(), rows.Count, null));
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Failed to parse paper_portfolio.json", detail: ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// Reads the Worker's PaperPortfolio JSON snapshot
    /// (<c>paper_portfolio.json</c>) so the dashboard can show live cash,
    /// equity, total PnL, daily PnL, win-rate etc. without going through
    /// the Trade table (which the hybrid executors don't currently populate).
    ///
    /// Snapshot is updated by <c>PaperPortfolioPersistenceService</c> after
    /// every trade close (10s debounce). When file doesn't exist yet,
    /// returns a stable zeroed payload.
    /// </summary>
    private static async Task<IResult> GetPortfolioSnapshotAsync(
        [Microsoft.AspNetCore.Mvc.FromServices] IExchangeService exchange,
        CancellationToken ct)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant",
            "paper_portfolio.json");

        // Live fallback: when the worker hasn't written paper_portfolio.json yet (no
        // open/close since last restart), derive a usable snapshot from the live OKX
        // balance + state-file open positions so the dashboard rings stop reading $0.
        if (!File.Exists(path))
        {
            try
            {
                var account = await exchange.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
                var bal = account.Balance;
                var openCount = CountOpenPositionsFromStateFiles();
                return Results.Ok(new PaperPortfolioSnapshotDto(
                    Found: true,
                    FilePath: path,
                    LastWriteUtc: DateTime.UtcNow,
                    InitialBalance: bal,
                    Cash: bal,
                    TotalEquity: account.Equity,
                    TotalPnL: 0m,
                    TotalPnLPercent: 0d,
                    DailyPnL: 0m,
                    OpenCount: openCount,
                    ClosedCount: 0,
                    WinningTrades: 0,
                    WinRate: 0d,
                    Note: $"Live fallback (no snapshot written yet). Equity from {account.Provider} account, open count from worker state files."));
            }
            catch
            {
                // Final fallback to zeroed payload so the dashboard doesn't error.
                return Results.Ok(new PaperPortfolioSnapshotDto(
                    Found: false, FilePath: path, LastWriteUtc: null,
                    InitialBalance: 0m, Cash: 0m, TotalEquity: 0m, TotalPnL: 0m,
                    TotalPnLPercent: 0d, DailyPnL: 0m,
                    OpenCount: 0, ClosedCount: 0, WinningTrades: 0, WinRate: 0d,
                    Note: "paper_portfolio.json not yet written and live exchange query failed."));
            }
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            decimal Read(string n) =>
                root.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? v.GetDecimal() : 0m;
            int CountArray(string n) =>
                root.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? v.GetArrayLength() : 0;

            var initial = Read("InitialBalance");
            var cash    = Read("Cash");
            var dailyPnL = Read("DailyPnL");

            // Walk ClosedPositions to compute realized total + WR
            decimal closedPnL = 0m;
            int wins = 0, closed = 0;
            if (root.TryGetProperty("ClosedPositions", out var cp) && cp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                closed = cp.GetArrayLength();
                foreach (var p in cp.EnumerateArray())
                {
                    if (p.TryGetProperty("RealizedPnL", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var pnl = rv.GetDecimal();
                        closedPnL += pnl;
                        if (pnl > 0m) wins++;
                    }
                }
            }

            // Walk OpenPositions for unrealized estimation
            decimal unrealised = 0m;
            int open = CountArray("OpenPositions");
            if (root.TryGetProperty("OpenPositions", out var op) && op.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var p in op.EnumerateArray())
                {
                    if (p.TryGetProperty("UnrealizedPnL", out var uv) && uv.ValueKind == System.Text.Json.JsonValueKind.Number)
                        unrealised += uv.GetDecimal();
                }
            }

            var totalEquity = cash + unrealised;
            var totalPnL    = totalEquity - initial;
            var totalPnLPct = initial > 0 ? (double)(totalPnL / initial * 100m) : 0d;
            var wr          = closed > 0 ? wins * 100d / closed : 0d;

            return Results.Ok(new PaperPortfolioSnapshotDto(
                Found:            true,
                FilePath:         path,
                LastWriteUtc:     File.GetLastWriteTimeUtc(path),
                InitialBalance:   initial,
                Cash:             cash,
                TotalEquity:      totalEquity,
                TotalPnL:         totalPnL,
                TotalPnLPercent:  totalPnLPct,
                DailyPnL:         dailyPnL,
                OpenCount:        open,
                ClosedCount:      closed,
                WinningTrades:    wins,
                WinRate:          wr,
                Note:             null));
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Failed to parse paper_portfolio.json", detail: ex.Message, statusCode: 500);
        }
    }

    private static IResult GetStatePositions()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        var files = new[]
        {
            ("Funding Long",  "funding_long_state.json"),
            ("Funding Short", "funding_short_state.json"),
            ("Pairs Long",    "pairs_long_state.json"),
            ("Pairs Short",   "pairs_short_state.json"),
        };

        var positions = new List<StatePositionDto>();
        var sources   = new List<StateSourceDto>();

        foreach (var (label, fname) in files)
        {
            var path = Path.Combine(dir, fname);
            if (!File.Exists(path))
            {
                sources.Add(new StateSourceDto(Tier: label, FileName: fname, Found: false, Count: 0, LastWriteUtc: null));
                continue;
            }
            var lastWrite = File.GetLastWriteTimeUtc(path);
            var rows = ReadStateFile(path, label);
            positions.AddRange(rows);
            sources.Add(new StateSourceDto(Tier: label, FileName: fname, Found: true, Count: rows.Count, LastWriteUtc: lastWrite));
        }

        // Sort newest entry first
        positions = positions
            .OrderByDescending(p => p.EntryTimeUtc)
            .ToList();

        return Results.Ok(new StatePositionsResponseDto(
            Sources:   sources.ToArray(),
            Positions: positions.ToArray(),
            Count:     positions.Count,
            FetchedUtc: DateTime.UtcNow));
    }

    private static List<StatePositionDto> ReadStateFile(string path, string tier)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new();
            var list = new List<StatePositionDto>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var v = prop.Value;
                // Tolerate both schemas (Funding hybrid uses `Qty`, others may use `Quantity`)
                decimal qty = TryGetDecimal(v, "Qty") ?? TryGetDecimal(v, "Quantity") ?? 0m;
                decimal entry = TryGetDecimal(v, "EntryPrice") ?? 0m;
                int side = TryGetInt(v, "Side") ?? 0;
                var sym = TryGetString(v, "Symbol") ?? prop.Name;
                var entryTime = TryGetString(v, "EntryTimeUtc");
                var mode = TryGetString(v, "ExecutionMode") ?? "PAPER";
                var clientOrderId = TryGetString(v, "ClientOrderId");
                var entryFunding = TryGetDecimal(v, "EntryFunding");
                var maxFav = TryGetDecimal(v, "MaxFavorablePrice");

                list.Add(new StatePositionDto(
                    Tier:           tier,
                    Symbol:         sym,
                    Side:           side == 0 ? "LONG" : "SHORT",
                    Quantity:       qty,
                    EntryPrice:     entry,
                    EntryFunding:   entryFunding,
                    MaxFavorablePrice: maxFav,
                    EntryTimeUtc:   ParseDateTime(entryTime),
                    ExecutionMode:  mode,
                    ClientOrderId:  clientOrderId));
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Counts open positions across all four hybrid-executor state files. Used by
    /// the portfolio fallback path so the dashboard knows how many trades are live
    /// even before the worker has written paper_portfolio.json for the first time.
    /// </summary>
    private static int CountOpenPositionsFromStateFiles()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        var files = new[] {
            "funding_long_state.json", "funding_short_state.json",
            "pairs_long_state.json",   "pairs_short_state.json",
        };
        var total = 0;
        foreach (var fname in files)
        {
            var p = Path.Combine(dir, fname);
            if (!File.Exists(p)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.Object) total++;
                }
            }
            catch { /* tolerate corrupt file */ }
        }
        return total;
    }

    private static decimal? TryGetDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var d)) return d;
        return null;
    }
    private static int? TryGetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
    private static string? TryGetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static DateTime ParseDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : DateTime.MinValue;
    }

    /// <summary>
    /// Live OKX (or other venue) account positions. Balance comes from
    /// IExchangeService.GetAccountStatusAsync (which only populates balance/equity for OKX),
    /// while open positions come from IExchangePositionReader.GetOpenPositionsAsync — the
    /// dedicated /api/v5/account/positions reader the worker reconciliation already uses.
    /// </summary>
    private static async Task<IResult> GetExchangePositions(
        [Microsoft.AspNetCore.Mvc.FromServices] IExchangeService exchange,
        [Microsoft.AspNetCore.Mvc.FromServices] IExchangePositionReader positionReader,
        [Microsoft.AspNetCore.Mvc.FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var accountTask = exchange.GetAccountStatusAsync().WaitAsync(ct);
            var positionsTask = positionReader.GetOpenPositionsAsync(ct);
            await Task.WhenAll(accountTask, positionsTask).ConfigureAwait(false);
            var account = accountTask.Result;
            var positions = positionsTask.Result;
            return Results.Ok(new
            {
                provider    = account.Provider,
                accountId   = account.Id,
                balance     = account.Balance,
                equity      = account.Equity,
                positions   = positions
                                .Select(p => new
                                {
                                    symbol            = p.Symbol,
                                    quantity          = p.IsLong ? p.Quantity : -p.Quantity,
                                    averageEntryPrice = p.AvgEntryPrice ?? 0m,
                                    unrealizedPnL     = (decimal?)null,
                                    side              = p.IsLong ? "LONG" : "SHORT",
                                })
                                .ToArray(),
                fetchedUtc  = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            var log = loggerFactory.CreateLogger("PaperStateEndpoints.GetExchangePositions");
            log.LogWarning(ex, "Exchange positions fetch failed");
            return Results.Problem(title: "Exchange query failed", detail: ex.Message, statusCode: 502);
        }
    }
}

// ─── DTOs ────────────────────────────────────────────────────────────────────

public sealed record StatePositionsResponseDto(
    StateSourceDto[]   Sources,
    StatePositionDto[] Positions,
    int                Count,
    DateTime           FetchedUtc);

public sealed record StateSourceDto(
    string    Tier,
    string    FileName,
    bool      Found,
    int       Count,
    DateTime? LastWriteUtc);

public sealed record PaperPortfolioSnapshotDto(
    bool      Found,
    string    FilePath,
    DateTime? LastWriteUtc,
    decimal   InitialBalance,
    decimal   Cash,
    decimal   TotalEquity,
    decimal   TotalPnL,
    double    TotalPnLPercent,
    decimal   DailyPnL,
    int       OpenCount,
    int       ClosedCount,
    int       WinningTrades,
    double    WinRate,
    string?   Note);

public sealed record ClosedTradesResponseDto(
    ClosedTradeDto[] Trades,
    int              Count,
    string?          Note);

public sealed record ClosedTradeDto(
    string   Symbol,
    string   Side,
    decimal  Quantity,
    decimal  EntryPrice,
    decimal  ExitPrice,
    decimal  RealizedPnL,
    DateTime EntryTimeUtc,
    DateTime ExitTimeUtc,
    string   StrategyTag,
    string   ExecutionMode,
    string   ExitReason);

public sealed record StatePositionDto(
    string    Tier,
    string    Symbol,
    string    Side,
    decimal   Quantity,
    decimal   EntryPrice,
    decimal?  EntryFunding,
    decimal?  MaxFavorablePrice,
    DateTime  EntryTimeUtc,
    string    ExecutionMode,
    string?   ClientOrderId);
