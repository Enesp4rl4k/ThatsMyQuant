using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Exceptions;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Options;
using System.Net.Http.Headers;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Application.Observability;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// OKX Exchange execution service implementing IExchangeService.
/// Uses OKX REST API v5 with HMAC-SHA256 + Base64 signature scheme.
/// Symbol normalization: BTCUSDT → BTC-USDT (OKX uses hyphenated format).
/// </summary>
public sealed class OkxExecutionService : IExchangeService, IExchangeOrderStateProvider, IVenueTradeFeeProvider, IExchangePositionReader, IDisposable
{
    private readonly ILogger<OkxExecutionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ApiVault? _apiVault;
    private string _currentSecret = string.Empty;
    private HMACSHA256? _hmac;
    private const string BaseUrl = "https://www.okx.com";
    private const VenueKind Venue = VenueKind.Okx;

    private string ApiKey => _configuration["OKX:ApiKey"] ?? string.Empty;
    private string Passphrase => _configuration["OKX:Passphrase"] ?? string.Empty;
    private bool IsDemoMode => _configuration.GetValue<bool>("OKX:IsDemoMode");
    private bool IsSwapMode
    {
        get
        {
            var mode = _configuration["TMQ_EXCHANGE_MODE"];
            return !string.IsNullOrWhiteSpace(mode) &&
                   mode.Contains("SWAP", StringComparison.OrdinalIgnoreCase);
        }
    }
    /// <summary>
    /// OKX position mode.
    /// hedge mode (true): each instrument has separate long/short pockets; posSide = "long"/"short".
    /// one-way / net mode (false, DEFAULT for new OKX accounts): one pocket per instrument; posSide = "net".
    /// Configure via OKX:HedgeMode = true if you manually switched the OKX account to hedge mode.
    /// </summary>
    private bool IsHedgeMode => _configuration.GetValue<bool>("OKX:HedgeMode");

    private readonly ConcurrentDictionary<string, OrderBookSnapshot> _latestOrderBooks = new();
    private readonly ISharedPortfolioStateManager _portfolio;
    private readonly OperationalTelemetry? _telemetry;
    private readonly OrderExecutionOptions _execOptions;

    // OKX perpetual swap contract spec cache: instId → (ctVal, lotSz, minSz)
    // ctVal: base-asset units per contract (e.g. 1 AVAX, 100 XRP, 100 ADA)
    // lotSz: sz must be a multiple of lotSz
    // minSz: minimum order size (in contracts)
    private record OkxContractSpec(decimal CtVal, decimal LotSz, decimal MinSz);
    private readonly ConcurrentDictionary<string, OkxContractSpec> _contractSpecCache = new();

    public OkxExecutionService(
        ILogger<OkxExecutionService> logger,
        HttpClient httpClient,
        IConfiguration configuration,
        ISharedPortfolioStateManager portfolioStateManager,
        ApiVault? apiVault = null,
        OperationalTelemetry? operationalTelemetry = null,
        IStreamingMarketDataService? streamingService = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _configuration = configuration;
        _apiVault = apiVault;
        _portfolio = portfolioStateManager;
        _telemetry = operationalTelemetry;
        _execOptions = new OrderExecutionOptions();
        configuration.GetSection(OrderExecutionOptions.SectionName).Bind(_execOptions);
        _execOptions.Normalize();

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (streamingService is OkxWebSocketService owss)
        {
            owss.OnOrderBookUpdated += snapshot =>
            {
                var keyInstrument = ResolveOkxInstrumentId(snapshot.Symbol);
                _latestOrderBooks[keyInstrument] = snapshot;
                if (!string.Equals(keyInstrument, snapshot.Symbol, StringComparison.OrdinalIgnoreCase))
                    _latestOrderBooks[snapshot.Symbol] = snapshot;
            };
        }
    }

    public bool TryGetLatestOrderBook(string symbol, out OrderBookSnapshot book)
    {
        if (_latestOrderBooks.TryGetValue(ResolveOkxInstrumentId(symbol), out var b))
        {
            book = b;
            return true;
        }

        if (_latestOrderBooks.TryGetValue(symbol, out b))
        {
            book = b;
            return true;
        }

        book = default;
        return false;
    }

    public async Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side)
    {
        var okxSymbol = ResolveOkxInstrumentId(symbol);
        if (!_latestOrderBooks.TryGetValue(okxSymbol, out var book) &&
            !_latestOrderBooks.TryGetValue(symbol, out book))
        {
            return 0;
        }

        return await Task.FromResult(QueuePositionCalculator.CalculateVolumeAhead(book, price, side));
    }

    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        var clientOrderId = ExecutionOrderIdentity.EnsureClientOrderId(signal);

        if (!IsSwapMode)
        {
            // Institutional Guard: Local balance check via MemoryPortfolioManager
            var requiredNotional = signal.Quantity * signal.Price;
            var reserved = _portfolio.TryReserveForOrder(Venue, "USDT", clientOrderId, requiredNotional);
            if (!reserved)
            {
                // Backward compatibility for tests/mocks that still only configure TryReserve.
                reserved = _portfolio.TryReserve(Venue, "USDT", requiredNotional);
            }

            if (!reserved)
            {
                _logger.LogWarning("OKX: Insufficient localized funds (MemoryPortfolio) for {Symbol}", signal.Symbol);
                throw new InvalidOperationException("Insufficient localized funds.");
            }
        }

        var okxSymbol = ResolveOkxInstrumentId(signal.Symbol);
        _logger.LogInformation("Executing OKX order for {Symbol} (→{OkxSymbol}) @ {Price} [DEMO:{IsDemo}]",
            signal.Symbol, okxSymbol, signal.Price, IsDemoMode);
        var reduceOnly = signal.Metadata.TryGetValue("ReduceOnly", out var reduceOnlyRaw) &&
                         bool.TryParse(reduceOnlyRaw, out var reduceOnlyParsed) &&
                         reduceOnlyParsed;
        var leverage = ResolveLeverage(signal);
        var marginMode = ResolveMarginMode(signal);
        var tdMode = IsSwapMode
            ? (marginMode == MarginMode.Cross ? "cross" : "isolated")
            : "cash";
        // posSide: hedge mode → "long"/"short"; one-way mode → "net" (or null = omit same effect)
        // reduceOnly: only meaningful in one-way mode for closes; hedge mode uses posSide to close
        string? posSide = null;
        bool? reduceOnlyField = null;
        if (IsSwapMode)
        {
            if (IsHedgeMode)
            {
                posSide = ResolveSwapPosSide(signal.Side, reduceOnly);
                // reduceOnly is NOT used in hedge mode (posSide encodes open vs close)
            }
            else
            {
                // One-way / net mode: posSide must be "net"
                posSide = "net";
                // reduceOnly=true tells OKX this is a position-closing order
                reduceOnlyField = reduceOnly ? true : null;
            }
        }

        // OKX perpetual swap: sz is in contracts, not base-asset units.
        // Fetch ctVal/lotSz/minSz from OKX public instruments endpoint and convert.
        // Example: AVAX-USDT-SWAP ctVal=1, lotSz=0.1, minSz=0.1
        //          XRP-USDT-SWAP  ctVal=100, lotSz=1, minSz=1
        // Formula: contracts = qty(base) / ctVal, rounded to nearest lotSz, clamped to minSz.
        var orderSz = signal.Quantity; // default: base-asset qty (spot or non-swap)
        if (IsSwapMode)
        {
            var spec = await GetContractSpecAsync(okxSymbol).ConfigureAwait(false);
            var rawContracts = spec.CtVal > 0m ? signal.Quantity / spec.CtVal : signal.Quantity;
            // Round to nearest multiple of lotSz (round up to ensure we meet min)
            var rounded = spec.LotSz > 0m
                ? Math.Floor(rawContracts / spec.LotSz) * spec.LotSz
                : rawContracts;
            // If rounding down gives 0, try rounding up to one lotSz
            if (rounded < spec.LotSz && spec.LotSz > 0m)
                rounded = spec.LotSz;
            orderSz = Math.Max(rounded, spec.MinSz);
            _logger.LogInformation(
                "OKX swap contract sizing: {Symbol} baseQty={BaseQty:F6} ctVal={CtVal} lotSz={LotSz} → contracts={Sz}",
                okxSymbol, signal.Quantity, spec.CtVal, spec.LotSz, orderSz);
        }
        var szStr = orderSz.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

        // Bracket SL/TP attachment: when caller provided StopLossPx + TakeProfitPx in
        // signal.Metadata AND this is an opening order (not reduceOnly), attach OKX algo
        // orders so the position is protected even if the worker crashes. tpOrdPx/slOrdPx
        // = "-1" means market order on trigger (instant fill, no slippage cap). Trigger
        // price uses "last" (last traded) by default — change to "mark" if more conservative.
        //
        // Bracket TP/SL is now placed POST-FILL (see PlacePostFillAlgoBracketAsync below) so
        // the algo sz matches the ACTUAL filled qty. Inline attachAlgoOrds is kept null —
        // the old path attached brackets sized to the parent market sz, and OKX market
        // partial fills caused reverse-position drift each TP/SL fire.
        // OKX:DisableBracketAttach=true keeps both paths off (worker HoldOpen is the
        // sole TP/SL keeper) for the rare cases where post-fill placement also fails.
        var disableBracket = _configuration.GetValue<bool>("OKX:DisableBracketAttach");
        object[]? attachAlgoOrds = null;

        // OKX v5 order request body. OrderType.Market → IOC taker order (no price field,
        // sz interpreted as base qty for perp/margin and as quote notional for spot buy
        // by default unless tgtCcy is set). OrderType.Limit preserves legacy maker path.
        var isMarket = signal.OrderType == OrderType.Market;
        var orderBody = isMarket
            ? (object)new
            {
                instId = okxSymbol,
                tdMode,
                side = signal.Side == TradeSide.Buy ? "buy" : "sell",
                ordType = "market",
                clOrdId = clientOrderId,
                sz = szStr,
                posSide,
                reduceOnly = reduceOnlyField,
                attachAlgoOrds
            }
            : new
            {
                instId = okxSymbol,
                tdMode,
                side = signal.Side == TradeSide.Buy ? "buy" : "sell",
                // OKX v5 uses a dedicated ordType=post_only for maker-only orders.
                // If it would cross the book, the order is rejected rather than
                // silently filled as taker — protects the maker rebate edge.
                ordType = _execOptions.PostOnlyMode ? "post_only" : "limit",
                clOrdId = clientOrderId,
                px = signal.Price.ToString("F8"),
                sz = szStr,
                posSide,
                reduceOnly = reduceOnlyField,
                attachAlgoOrds
            };

        var bodyJson = JsonSerializer.Serialize(orderBody);
        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "POST", "/api/v5/trade/order", bodyJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/order");
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        // OKX-specific authentication headers
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);

        if (IsDemoMode)
        {
            request.Headers.Add("x-simulated-trading", "1");
        }

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("OKX execution failed: {Error}", error);
            throw new InvalidOperationException($"OKX execution failed: {error}");
        }

        // Check OKX response body for error codes (OKX returns 200 even on logical errors)
        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var code = doc.RootElement.GetProperty("code").GetString();
        if (code != "0")
        {
            var msg = doc.RootElement.GetProperty("msg").GetString();
            // OKX batch API: each order result is in data[]. Even for a single order,
            // the real error is in data[0].sCode + data[0].sMsg — not the root msg.
            var detailCode = "";
            var detailMsg = "";
            try
            {
                var dataArr = doc.RootElement.GetProperty("data");
                if (dataArr.GetArrayLength() > 0)
                {
                    var d0 = dataArr[0];
                    if (d0.TryGetProperty("sCode", out var sc)) detailCode = sc.GetString() ?? "";
                    if (d0.TryGetProperty("sMsg", out var sm))  detailMsg  = sm.GetString() ?? "";
                }
            }
            catch { /* ignore parse errors */ }

            // Self-healing: when we send a CLOSE (reduceOnly=true) but the position is
            // already gone — typically because OKX-side bracket TP/SL fired and closed
            // it before our funding-driven close arrived — OKX returns sCode=51169
            // "no position to reduce". Treat as success so the executor clears its
            // local state instead of leaving a dead "ghost" entry forever.
            if (reduceOnly && detailCode == "51169")
            {
                _logger.LogInformation(
                    "OKX close skipped for {Symbol}: position already closed (likely OKX bracket TP/SL fired). Returning synthetic success so caller clears local state.",
                    okxSymbol);
                return new Trade
                {
                    Id = Guid.NewGuid(),
                    SignalId = signal.Id,
                    Symbol = signal.Symbol,
                    Side = signal.Side,
                    EntryPrice = signal.Price,
                    ExitPrice = signal.Price,
                    Quantity = 0m,
                    FilledQuantity = 0m,
                    RequestedQuantity = signal.Quantity,
                    RemainingQuantity = 0m,
                    EntryTime = DateTime.UtcNow,
                    SubmittedTime = DateTime.UtcNow,
                    LastUpdatedTime = DateTime.UtcNow,
                    Status = TradeStatus.Executed,
                    LifecycleStatus = TradeLifecycleStatus.Closed,
                    ExecutionMode = IsSwapMode
                        ? (IsDemoMode ? "DEMO-OKX-SWAP-BRACKET" : "REAL-OKX-SWAP-BRACKET")
                        : (IsDemoMode ? "DEMO-OKX" : "REAL-OKX"),
                    Leverage = leverage,
                    MarginMode = marginMode,
                    TotalFeesPaid = 0m,
                    NetFunding = 0m
                };
            }

            _logger.LogError("OKX order rejected: code={Code} msg={Msg} | sCode={SCode} sMsg={SMsg} | body={Body}",
                code, msg, detailCode, detailMsg, responseJson);
            throw new InvalidOperationException($"OKX order rejected: {code} - {msg} (sCode={detailCode} sMsg={detailMsg})");
        }

        // Funds were atomically reserved locally before the REST call.

        // Parse ordId from placement response and poll for fill status
        var ordId = string.Empty;
        try
        {
            ordId = doc.RootElement.GetProperty("data")[0].GetProperty("ordId").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX: Could not parse ordId from placement response.");
        }

        decimal filledQty = 0m;
        decimal remainingQty = signal.Quantity;
        var fillStatus = TradeStatus.Pending;

        decimal fillFeeSigned = 0m;
        if (!string.IsNullOrEmpty(ordId))
        {
            (filledQty, remainingQty, fillStatus, fillFeeSigned) = await PollFillStatusAsync(ordId, okxSymbol, signal.Quantity);
        }

        // --- Limit-order timeout → Market fallback -----------------------------------
        // Applies only when the original order was a limit and the operator opted in
        // via OrderExecution:LimitOrderTimeoutSeconds. Partial fills are accepted as-is
        // and we only re-submit the *remaining* quantity as a market order.
        if (!isMarket
            && fillStatus == TradeStatus.Pending
            && !string.IsNullOrEmpty(ordId)
            && _execOptions.LimitOrderTimeoutSeconds > 0)
        {
            var waited = await WaitForLimitFillOrTimeoutAsync(ordId, okxSymbol, signal.Quantity);
            filledQty = waited.FilledQty;
            remainingQty = waited.RemainingQty;
            fillStatus = waited.Status;
            fillFeeSigned += waited.FillFeeSigned;

            if (fillStatus == TradeStatus.Pending && remainingQty > 0m)
            {
                _logger.LogWarning(
                    "OKX: limit order {OrdId} for {Symbol} did not fill within {Timeout}s — cancelling and re-submitting remaining {Qty} as MARKET.",
                    ordId, okxSymbol, _execOptions.LimitOrderTimeoutSeconds, remainingQty);

                var cancelled = await TryCancelOrderAsync(okxSymbol, ordId);
                if (cancelled)
                {
                    var mktResult = await SubmitMarketFallbackAsync(signal, okxSymbol, remainingQty, tdMode, posSide, reduceOnlyField);
                    filledQty += mktResult.FilledQty;
                    remainingQty = mktResult.RemainingQty;
                    fillStatus = mktResult.Status;
                    fillFeeSigned += mktResult.FillFeeSigned;
                }
            }
        }

        // OKX fillFee: positive = fee paid (quote); negative = rebate (cash in).
        var feePaid = fillFeeSigned > 0m ? fillFeeSigned : 0m;

        // POST-FILL OCO BRACKET — partial-fill safe replacement for the deprecated
        // inline attachAlgoOrds path. Sized to the ACTUAL filled qty (in CONTRACTS, before
        // the base-unit conversion below) and submitted reduce-only, so a TP/SL fire
        // closes EXACTLY the open position rather than over-selling and opening a reverse.
        if (!disableBracket && IsSwapMode && !reduceOnly && filledQty > 0m
            && signal.Metadata.TryGetValue("StopLossPx", out var slPxRaw)
            && signal.Metadata.TryGetValue("TakeProfitPx", out var tpPxRaw)
            && decimal.TryParse(slPxRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var slPx) && slPx > 0m
            && decimal.TryParse(tpPxRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var tpPx) && tpPx > 0m)
        {
            try
            {
                await PlacePostFillAlgoBracketAsync(okxSymbol, tdMode, posSide, signal.Side, filledQty, slPx, tpPx).ConfigureAwait(false);
            }
            catch (Exception bracketEx)
            {
                // Entry already filled — log and let worker HoldOpen TP/SL guard the position.
                _logger.LogWarning(bracketEx,
                    "OKX post-fill bracket placement failed for {Symbol} (filledQty={Qty} contracts). Worker HoldOpen will manage exit.",
                    okxSymbol, filledQty);
            }
        }

        // OKX swap fills are reported in CONTRACTS. Internal callers (FundingHybridExecutor,
        // PairsHybridExecutor, reconciliation) all reason in BASE-ASSET units. Convert here
        // so the rest of the system never has to know contracts exist.
        // Example: ADA-USDT-SWAP fillQty=0.1 contracts × ctVal=100 ADA = 10 ADA stored.
        decimal filledQtyBase = filledQty;
        decimal remainingQtyBase = remainingQty;
        decimal requestedQtyBase = signal.Quantity;
        if (IsSwapMode)
        {
            try
            {
                var spec = await GetContractSpecAsync(okxSymbol).ConfigureAwait(false);
                if (spec.CtVal > 0m)
                {
                    filledQtyBase = filledQty * spec.CtVal;
                    remainingQtyBase = remainingQty * spec.CtVal;
                    // requestedQtyBase stays in base units (signal.Quantity is already base)
                }
            }
            catch { /* fall back to raw fill qty if spec lookup fails */ }
        }

        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            Quantity = filledQtyBase,
            FilledQuantity = filledQtyBase,
            RequestedQuantity = requestedQtyBase,
            RemainingQuantity = remainingQtyBase,
            EntryTime = DateTime.UtcNow,
            SubmittedTime = DateTime.UtcNow,
            LastUpdatedTime = DateTime.UtcNow,
            Status = fillStatus,
            LifecycleStatus = TradeLifecycleStatus.Accepted,
            ExecutionMode = IsSwapMode
                ? (IsDemoMode ? "DEMO-OKX-SWAP" : "REAL-OKX-SWAP")
                : (IsDemoMode ? "DEMO-OKX" : "REAL-OKX"),
            Leverage = leverage,
            MarginMode = marginMode,
            TotalFeesPaid = feePaid,
            NetFunding = 0m
        };
    }

    private static decimal ResolveLeverage(Signal signal)
    {
        if (signal.Metadata.TryGetValue("Leverage", out var leverageRaw) &&
            decimal.TryParse(leverageRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) &&
            leverage > 0m)
        {
            return leverage;
        }

        return 1m;
    }

    private static MarginMode ResolveMarginMode(Signal signal)
    {
        if (signal.Metadata.TryGetValue("MarginMode", out var marginRaw) &&
            Enum.TryParse<MarginMode>(marginRaw, true, out var marginMode))
        {
            return marginMode;
        }

        return MarginMode.Isolated;
    }

    /// <summary>
    /// Places a standalone OCO (one-cancels-other) algo order on OKX after the entry
    /// fill is confirmed. Sized to the actual filled qty in CONTRACTS, reduceOnly=true.
    /// Replaces the deprecated inline attachAlgoOrds path that suffered from partial-fill
    /// drift (parent sz != filled sz → bracket fires over-sells → reverse position opens).
    ///
    /// Failure here is logged but not thrown: entry has already filled, so the worker's
    /// HoldOpen tick-loop remains as the TP/SL safety net.
    /// </summary>
    private async Task PlacePostFillAlgoBracketAsync(
        string instId,
        string tdMode,
        string? posSide,
        TradeSide entrySide,
        decimal sizeContracts,
        decimal slPx,
        decimal tpPx)
    {
        // Closing side: opposite of entry. Long → sell, Short → buy.
        var closeSide = entrySide == TradeSide.Buy ? "sell" : "buy";

        var algoBody = new
        {
            instId,
            tdMode,
            side = closeSide,
            posSide,
            ordType = "oco",
            sz = sizeContracts.ToString("G", CultureInfo.InvariantCulture),
            reduceOnly = "true",
            tpTriggerPx     = tpPx.ToString("G", CultureInfo.InvariantCulture),
            tpOrdPx         = "-1",
            tpTriggerPxType = "last",
            slTriggerPx     = slPx.ToString("G", CultureInfo.InvariantCulture),
            slOrdPx         = "-1",
            slTriggerPxType = "last"
        };

        var body = JsonSerializer.Serialize(algoBody);
        var ts = GetIsoTimestamp();
        var sig = ComputeSignature(ts, "POST", "/api/v5/trade/order-algo", body);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/order-algo");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Add("OK-ACCESS-KEY", ApiKey);
        req.Headers.Add("OK-ACCESS-SIGN", sig);
        req.Headers.Add("OK-ACCESS-TIMESTAMP", ts);
        req.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        if (IsDemoMode) req.Headers.Add("x-simulated-trading", "1");

        var resp = await _httpClient.SendAsync(req).ConfigureAwait(false);
        var respJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OKX post-fill algo HTTP {(int)resp.StatusCode}: {respJson}");
        }

        using var doc = JsonDocument.Parse(respJson);
        var code = doc.RootElement.GetProperty("code").GetString();
        if (code != "0")
        {
            var msg = doc.RootElement.GetProperty("msg").GetString();
            var sCode = string.Empty;
            var sMsg  = string.Empty;
            try
            {
                var dataArr = doc.RootElement.GetProperty("data");
                if (dataArr.GetArrayLength() > 0)
                {
                    var d0 = dataArr[0];
                    if (d0.TryGetProperty("sCode", out var sc)) sCode = sc.GetString() ?? string.Empty;
                    if (d0.TryGetProperty("sMsg",  out var sm)) sMsg  = sm.GetString() ?? string.Empty;
                }
            }
            catch { /* ignore parse errors */ }
            throw new InvalidOperationException(
                $"OKX post-fill algo rejected: code={code} msg={msg} sCode={sCode} sMsg={sMsg}");
        }

        var algoId = string.Empty;
        try { algoId = doc.RootElement.GetProperty("data")[0].GetProperty("algoId").GetString() ?? string.Empty; }
        catch { /* algoId is informational only */ }

        _logger.LogInformation(
            "OKX post-fill OCO bracket placed: {Symbol} sz={Sz} contracts SL={SL} TP={TP} algoId={AlgoId}",
            instId, sizeContracts, slPx, tpPx, algoId);
    }

    /// <summary>
    /// Polls OKX GET /api/v5/trade/order for fill status.
    /// Max 3 attempts with 500ms delay. Returns (0, requestedQty, Pending) on any failure.
    /// </summary>
    private async Task<(decimal FilledQty, decimal RemainingQty, TradeStatus Status, decimal FillFeeSigned)> PollFillStatusAsync(
        string ordId,
        string instId,
        decimal requestedQty,
        CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        const int delayMs = 500;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var path = $"/api/v5/trade/order?instId={instId}&ordId={ordId}";
                var timestamp = GetIsoTimestamp();
                var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
                request.Headers.Add("OK-ACCESS-KEY", ApiKey);
                request.Headers.Add("OK-ACCESS-SIGN", signature);
                request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
                request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
                if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    TrackHttpFailure(response);
                    _logger.LogError("OKX fill poll HTTP {Status} for ordId={OrdId}", response.StatusCode, ordId);
                    if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0)
                {
                    if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var order = data[0];
                var fillSz = ParseDecimal(order, "fillSz");
                var fillFeeSigned = ParseDecimal(order, "fillFee");
                var state  = order.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "live";

                return state switch
                {
                    "filled"           => (fillSz, 0m, TradeStatus.Executed, fillFeeSigned),
                    "partially_filled" => (fillSz, requestedQty - fillSz, TradeStatus.Pending, fillFeeSigned),
                    _                  => (0m, requestedQty, TradeStatus.Pending, 0m)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OKX fill poll exception on attempt {Attempt} for ordId={OrdId}", attempt, ordId);
                if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
            }
        }

        return (0m, requestedQty, TradeStatus.Pending, 0m);
    }

    /// <summary>
    /// Polls a limit order's fill state until either it fills or the configured
    /// <see cref="OrderExecutionOptions.LimitOrderTimeoutSeconds"/> budget is exhausted.
    /// Uses <see cref="OrderExecutionOptions.LimitFillPollIntervalMs"/> between samples.
    /// </summary>
    private async Task<(decimal FilledQty, decimal RemainingQty, TradeStatus Status, decimal FillFeeSigned)> WaitForLimitFillOrTimeoutAsync(
        string ordId,
        string instId,
        decimal requestedQty,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_execOptions.LimitOrderTimeoutSeconds);
        var pollMs = _execOptions.LimitFillPollIntervalMs;

        decimal lastFilled = 0m;
        decimal lastRemaining = requestedQty;
        var lastStatus = TradeStatus.Pending;
        decimal lastFee = 0m;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            (lastFilled, lastRemaining, lastStatus, lastFee) = await PollFillStatusAsync(ordId, instId, requestedQty, cancellationToken).ConfigureAwait(false);
            if (lastStatus == TradeStatus.Executed || lastRemaining <= 0m) break;
            try
            {
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        return (lastFilled, lastRemaining, lastStatus, lastFee);
    }

    private async Task<bool> TryCancelOrderAsync(string instId, string ordId, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { instId, ordId });
            var timestamp = GetIsoTimestamp();
            var signature = ComputeSignature(timestamp, "POST", "/api/v5/trade/cancel-order", body);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/cancel-order");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.Add("OK-ACCESS-KEY", ApiKey);
            req.Headers.Add("OK-ACCESS-SIGN", signature);
            req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            req.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode) req.Headers.Add("x-simulated-trading", "1");

            var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                TrackHttpFailure(resp);
                _logger.LogWarning("OKX: cancel order {OrdId} failed HTTP {Status}.", ordId, resp.StatusCode);
                return false;
            }
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.GetProperty("code").GetString();
            return code == "0";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX: exception while cancelling order {OrdId}.", ordId);
            return false;
        }
    }

    private async Task<(decimal FilledQty, decimal RemainingQty, TradeStatus Status, decimal FillFeeSigned)> SubmitMarketFallbackAsync(
        Signal original,
        string okxSymbol,
        decimal remainingQty,
        string tdMode,
        string? posSide,
        bool? reduceOnlyField,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fallbackClientId = $"{ExecutionOrderIdentity.EnsureClientOrderId(original)}M";
            var body = JsonSerializer.Serialize(new
            {
                instId = okxSymbol,
                tdMode,
                side = original.Side == TradeSide.Buy ? "buy" : "sell",
                ordType = "market",
                clOrdId = fallbackClientId,
                sz = remainingQty.ToString("F8"),
                posSide,
                reduceOnly = reduceOnlyField
            });

            var timestamp = GetIsoTimestamp();
            var signature = ComputeSignature(timestamp, "POST", "/api/v5/trade/order", body);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/order");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.Add("OK-ACCESS-KEY", ApiKey);
            req.Headers.Add("OK-ACCESS-SIGN", signature);
            req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            req.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode) req.Headers.Add("x-simulated-trading", "1");

            var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                TrackHttpFailure(resp);
                _logger.LogError("OKX market fallback failed: HTTP {Status}.", resp.StatusCode);
                return (0m, remainingQty, TradeStatus.Pending, 0m);
            }
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                _logger.LogError("OKX market fallback rejected: {Msg}",
                    doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "<no msg>");
                return (0m, remainingQty, TradeStatus.Pending, 0m);
            }

            var fbOrdId = doc.RootElement.GetProperty("data")[0].GetProperty("ordId").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(fbOrdId))
                return (0m, remainingQty, TradeStatus.Pending, 0m);

            return await PollFillStatusAsync(fbOrdId, okxSymbol, remainingQty, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX: exception during market fallback submission.");
            return (0m, remainingQty, TradeStatus.Pending, 0m);
        }
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        // Use localized portfolio state if already synced
        var availableUsdt = _portfolio.GetAvailableBalance(Venue, "USDT");
        if (availableUsdt > 0)
        {
            return new Account
            {
                Id = "OKX-LIVE-CACHED",
                Provider = "OKX",
                Balance = availableUsdt,
                Equity = availableUsdt
            };
        }

        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "GET", "/api/v5/account/balance", string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v5/account/balance");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        // OKX requires x-simulated-trading=1 on the balance endpoint too — without it
        // demo accounts return the (empty) live book and the header badge shows 0 USDT.
        if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to fetch OKX account status: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        decimal usdtBalance = 0;
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() > 0)
        {
            var details = data[0].GetProperty("details");
            foreach (var detail in details.EnumerateArray())
            {
                var ccy = detail.GetProperty("ccy").GetString();
                if (ccy == "USDT")
                {
                    usdtBalance = decimal.Parse(
                        detail.GetProperty("availBal").GetString() ?? "0",
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        _portfolio.SetBalance(Venue, "USDT", usdtBalance);

        return new Account
        {
            Id = "OKX-LIVE",
            Provider = "OKX",
            Balance = usdtBalance,
            Equity = usdtBalance
        };
    }

    public async Task<IReadOnlyList<OpenOrderSnapshot>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = GetIsoTimestamp();
        var path = IsSwapMode
            ? "/api/v5/trade/orders-pending?instType=SWAP"
            : "/api/v5/trade/orders-pending?instType=SPOT";
        var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        if (IsDemoMode)
        {
            request.Headers.Add("x-simulated-trading", "1");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to fetch OKX open orders: {error}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetString() != "0")
        {
            var msg = root.GetProperty("msg").GetString();
            throw new InvalidOperationException($"OKX open orders query rejected: {msg}");
        }

        var snapshots = new List<OpenOrderSnapshot>();
        foreach (var item in root.GetProperty("data").EnumerateArray())
        {
            var requestedQuantity = ParseDecimal(item, "sz");
            var filledQuantity = ParseDecimal(item, "accFillSz");
            var remainingQuantity = Math.Max(0m, requestedQuantity - filledQuantity);
            var price = ParseDecimal(item, "px");
            var averagePrice = ParseDecimal(item, "avgPx", price);
            var side = item.TryGetProperty("side", out var sideElement) &&
                       string.Equals(sideElement.GetString(), "sell", StringComparison.OrdinalIgnoreCase)
                ? TradeSide.Sell
                : TradeSide.Buy;

            snapshots.Add(new OpenOrderSnapshot(
                NormalizeFromOkx(item.TryGetProperty("instId", out var symbolElement) ? symbolElement.GetString() ?? string.Empty : string.Empty),
                side,
                item.TryGetProperty("clOrdId", out var clientOrderIdElement) ? clientOrderIdElement.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("ordId", out var orderIdElement) ? orderIdElement.GetString() ?? string.Empty : string.Empty,
                requestedQuantity,
                filledQuantity,
                remainingQuantity,
                price,
                averagePrice,
                item.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? "live" : "live",
                ResolveOkxTimestamp(item)));
        }

        return snapshots;
    }

    private static readonly HashSet<string> SkippedFavoriteAssets =
        new(StringComparer.OrdinalIgnoreCase) { "USDT", "USDC", "USD", "BTC", "ETH", "TRY" };

    public async Task<List<string>> GetFavoritesAsync()
    {
        var timestamp = GetIsoTimestamp();
        const string path = "/api/v5/account/balance";
        var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            TrackHttpFailure(response);
            var error = await response.Content.ReadAsStringAsync();
            throw new ExchangeSyncException("OKX", $"HTTP {(int)response.StatusCode}: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.GetProperty("code").GetString();
        if (code != "0")
        {
            var msg = root.GetProperty("msg").GetString() ?? "Unknown error";
            throw new ExchangeSyncException("OKX", $"code={code}: {msg}");
        }

        var results = new List<string>();
        foreach (var account in root.GetProperty("data").EnumerateArray())
        {
            if (!account.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var detail in details.EnumerateArray())
            {
                var asset = detail.TryGetProperty("ccy", out var ccyElement)
                    ? ccyElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(asset) || SkippedFavoriteAssets.Contains(asset))
                {
                    continue;
                }

                var available = ParseDecimal(detail, "availBal");
                var cashBalance = ParseDecimal(detail, "cashBal");
                if (available + cashBalance <= 0m)
                {
                    continue;
                }

                var canonical = SymbolNormalizer.ToCanonical(asset + "USDT");
                if (SymbolNormalizer.IsValid(canonical))
                {
                    results.Add(canonical);
                }
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// OKX signature: Base64(HMAC-SHA256(timestamp + method + requestPath + body))
    /// When ApiVault is injected, the secret bytes are retrieved transiently and zeroed
    /// immediately after signing — no plaintext key lingers in memory.
    /// Falls back to IConfiguration["OKX:SecretKey"] when vault is null (e.g. in tests).
    /// </summary>
    private string ComputeSignature(string timestamp, string method, string requestPath, string body)
    {
        var prehash = $"{timestamp}{method}{requestPath}{body}";
        var prehashBytes = Encoding.UTF8.GetBytes(prehash);

        if (_apiVault != null)
        {
            // Vault path: get transient secret bytes, sign, then zero them out.
            byte[] secretBytes = _apiVault.GetTransientDecryptedKey();
            try
            {
                using var hmac = new HMACSHA256(secretBytes);
                return Convert.ToBase64String(hmac.ComputeHash(prehashBytes));
            }
            finally
            {
                Array.Clear(secretBytes, 0, secretBytes.Length);
            }
        }

        // Fallback path (backward compat / tests): use IConfiguration.
        var secret = _configuration["OKX:SecretKey"] ?? string.Empty;
        if (secret != _currentSecret || _hmac == null)
        {
            _hmac?.Dispose();
            _hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            _currentSecret = secret;
        }

        return Convert.ToBase64String(_hmac.ComputeHash(prehashBytes));
    }

    /// <summary>
    /// OKX requires ISO 8601 timestamp: e.g. "2024-01-01T00:00:00.000Z"
    /// </summary>
    private static string GetIsoTimestamp()
        => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Converts internal symbol format (BTCUSDT) to OKX format (BTC-USDT).
    /// Also handles symbols that are already in OKX format.
    /// </summary>
    public static string NormalizeToOkx(string symbol)
    {
        // Already OKX format
        if (symbol.Contains('-'))
            return symbol;

        // Common quote assets — try to split
        string[] quoteAssets = ["USDT", "USDC", "BTC", "ETH", "EUR", "TRY"];
        foreach (var quote in quoteAssets)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && symbol.Length > quote.Length)
            {
                var baseAsset = symbol[..^quote.Length];
                return $"{baseAsset}-{quote}";
            }
        }

        // Fallback: return as-is (may fail on OKX, but won't crash here)
        return symbol;
    }

    /// <summary>
    /// Converts OKX symbol format (BTC-USDT) back to internal format (BTCUSDT).
    /// </summary>
    public static string NormalizeFromOkx(string okxSymbol)
        => okxSymbol.Replace("-", string.Empty);

    private string ResolveOkxInstrumentId(string symbol)
    {
        var normalized = NormalizeToOkx(symbol);
        if (!IsSwapMode)
        {
            return normalized;
        }

        return normalized.EndsWith("-SWAP", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}-SWAP";
    }

    private static string ResolveSwapPosSide(TradeSide side, bool reduceOnly)
    {
        if (reduceOnly)
        {
            return side == TradeSide.Sell ? "long" : "short";
        }

        return side == TradeSide.Buy ? "long" : "short";
    }

    /// <summary>
    /// Fetches (and caches) the OKX perpetual swap contract specification for an instrument.
    /// Uses GET /api/v5/public/instruments?instType=SWAP&amp;instId={instId} (no auth needed).
    /// Returns sensible defaults (ctVal=1, lotSz=1, minSz=1) on any error so the caller can
    /// still attempt the order and learn the real rejection reason from OKX.
    /// </summary>
    private async Task<OkxContractSpec> GetContractSpecAsync(string instId, CancellationToken ct = default)
    {
        if (_contractSpecCache.TryGetValue(instId, out var cached))
            return cached;

        try
        {
            var path = $"/api/v5/public/instruments?instType=SWAP&instId={Uri.EscapeDataString(instId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
            // Public endpoint — no auth headers needed. Demo header not required either.
            var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("OKX instrument spec fetch failed for {InstId}: HTTP {Status}", instId, resp.StatusCode);
                return CacheAndReturn(instId, new OkxContractSpec(1m, 1m, 1m));
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0" ||
                doc.RootElement.GetProperty("data").GetArrayLength() == 0)
            {
                _logger.LogWarning("OKX instrument spec empty for {InstId}", instId);
                return CacheAndReturn(instId, new OkxContractSpec(1m, 1m, 1m));
            }

            var row = doc.RootElement.GetProperty("data")[0];
            var ctVal  = ParseDecimalStr(row, "ctVal",  1m);
            var lotSz  = ParseDecimalStr(row, "lotSz",  1m);
            var minSz  = ParseDecimalStr(row, "minSz",  1m);
            var spec = new OkxContractSpec(ctVal, lotSz, minSz);
            _logger.LogInformation("OKX contract spec {InstId}: ctVal={CtVal} lotSz={LotSz} minSz={MinSz}",
                instId, ctVal, lotSz, minSz);
            return CacheAndReturn(instId, spec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX: GetContractSpecAsync threw for {InstId} — using defaults.", instId);
            return CacheAndReturn(instId, new OkxContractSpec(1m, 1m, 1m));
        }

        OkxContractSpec CacheAndReturn(string id, OkxContractSpec s)
        {
            _contractSpecCache[id] = s;
            return s;
        }
    }

    private static decimal ParseDecimalStr(JsonElement element, string propertyName, decimal fallback = 0m)
    {
        if (!element.TryGetProperty(propertyName, out var prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v > 0m ? v : fallback;
        if (prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetDecimal(out var n))
            return n > 0m ? n : fallback;
        return fallback;
    }

    /// <inheritdoc />
    public async Task<decimal?> GetTakerCommissionPercentPerSideAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var instId = ResolveOkxInstrumentId(symbol);
        var instType = IsSwapMode ? "SWAP" : "SPOT";
        var path = $"/api/v5/account/trade-fee?instType={instType}&instId={Uri.EscapeDataString(instId)}";
        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
            request.Headers.Add("OK-ACCESS-KEY", ApiKey);
            request.Headers.Add("OK-ACCESS-SIGN", signature);
            request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode)
                request.Headers.Add("x-simulated-trading", "1");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                TrackHttpFailure(response);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
                return null;

            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return null;

            var row = data[0];
            if (!row.TryGetProperty("taker", out var takerEl))
                return null;

            var ts = takerEl.ValueKind == JsonValueKind.String ? takerEl.GetString() : takerEl.ToString();
            if (decimal.TryParse(ts, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                return rate * 100m;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OKX trade fee lookup failed for {Symbol}", symbol);
            return null;
        }
    }

    public async Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        int cancelledCount = 0;
        try
        {
            // OKX v5: Get pending orders
            var timestamp = GetIsoTimestamp();
            var pendingPath = IsSwapMode
                ? "/api/v5/trade/orders-pending?instType=SWAP"
                : "/api/v5/trade/orders-pending?instType=SPOT";
            var signature = ComputeSignature(timestamp, "GET", pendingPath, string.Empty);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{pendingPath}");
            request.Headers.Add("OK-ACCESS-KEY", ApiKey);
            request.Headers.Add("OK-ACCESS-SIGN", signature);
            request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                TrackHttpFailure(response);
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OKX: Failed to fetch pending orders for emergency cancel: {Error}", error);
                return 0;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                _logger.LogError("OKX: Pending orders query returned error: {Msg}",
                    doc.RootElement.GetProperty("msg").GetString());
                return 0;
            }

            var orders = doc.RootElement.GetProperty("data");
            foreach (var order in orders.EnumerateArray())
            {
                try
                {
                    var instId = order.GetProperty("instId").GetString() ?? "";
                    var ordId = order.GetProperty("ordId").GetString() ?? "";

                    var cancelBody = JsonSerializer.Serialize(new { instId, ordId });
                    var cancelTimestamp = GetIsoTimestamp();
                    var cancelSig = ComputeSignature(cancelTimestamp, "POST", "/api/v5/trade/cancel-order", cancelBody);

                    using var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v5/trade/cancel-order");
                    cancelReq.Content = new StringContent(cancelBody, Encoding.UTF8, "application/json");
                    cancelReq.Headers.Add("OK-ACCESS-KEY", ApiKey);
                    cancelReq.Headers.Add("OK-ACCESS-SIGN", cancelSig);
                    cancelReq.Headers.Add("OK-ACCESS-TIMESTAMP", cancelTimestamp);
                    cancelReq.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
                    if (IsDemoMode) cancelReq.Headers.Add("x-simulated-trading", "1");

                    var cancelResp = await _httpClient.SendAsync(cancelReq, cancellationToken);
                    if (cancelResp.IsSuccessStatusCode)
                    {
                        cancelledCount++;
                        _logger.LogWarning("🚨 Emergency cancelled OKX order {OrderId} for {InstId}", ordId, instId);
                    }
                    else
                    {
                        TrackHttpFailure(cancelResp);
                        var cancelErr = await cancelResp.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError("Failed to cancel OKX order {OrderId}: {Error}", ordId, cancelErr);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cancelling individual OKX order during emergency halt.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency CancelAllOrdersAsync failed on OKX.");
        }

        _logger.LogCritical("🚨 OKX emergency halt: {Count} orders cancelled.", cancelledCount);
        return cancelledCount;
    }

    private void TrackHttpFailure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        _telemetry?.RecordVenueHttpFailure("okx");
    }

    private static decimal ParseDecimal(JsonElement element, string propertyName, decimal fallback = 0m)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDecimal(out var d) ? d : fallback,
            JsonValueKind.String => decimal.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback,
            _ => fallback
        };
    }

    private static DateTime ResolveOkxTimestamp(JsonElement element)
    {
        foreach (var propertyName in new[] { "uTime", "cTime" })
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (long.TryParse(property.GetString(), out var epochMs) && epochMs > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            }
        }

        return DateTime.UtcNow;
    }

    public async Task<IReadOnlyList<ExchangePositionSnapshot>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        // OKX v5: GET /api/v5/account/positions returns SWAP/FUTURES/MARGIN positions.
        // Spot holdings live under /account/balance and are not surfaced here —
        // reconciliation for spot is covered by the balance path.
        const string path = "/api/v5/account/positions";
        var timestamp = GetIsoTimestamp();
        var signature = ComputeSignature(timestamp, "GET", path, string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Add("OK-ACCESS-KEY", ApiKey);
        request.Headers.Add("OK-ACCESS-SIGN", signature);
        request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        request.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
        if (IsDemoMode) request.Headers.Add("x-simulated-trading", "1");

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                TrackHttpFailure(response);
                _logger.LogWarning("OKX positions fetch failed: HTTP {Status}", response.StatusCode);
                return Array.Empty<ExchangePositionSnapshot>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                _logger.LogWarning("OKX positions returned non-zero code: {Msg}",
                    doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "<no msg>");
                return Array.Empty<ExchangePositionSnapshot>();
            }

            var data = doc.RootElement.GetProperty("data");
            var list = new List<ExchangePositionSnapshot>(data.GetArrayLength());
            foreach (var p in data.EnumerateArray())
            {
                var instId = p.TryGetProperty("instId", out var iid) ? iid.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(instId)) continue;

                var pos = ParseDecimal(p, "pos");
                if (pos == 0m) continue;

                var posSide = p.TryGetProperty("posSide", out var ps) ? ps.GetString() ?? "net" : "net";
                bool isLong = posSide switch
                {
                    "long" => true,
                    "short" => false,
                    _ => pos > 0m
                };
                var qty = Math.Abs(pos);
                var avg = ParseDecimal(p, "avgPx");
                // Reconciliation compares to internal symbols which use canonical format (LTCUSDT).
                // OKX returns instId in exchange format (LTC-USDT-SWAP). Strip "-SWAP" then
                // remove dashes so reconciliation can match. Also rescale qty from contracts
                // back to base-asset units (qty_base = contracts × ctVal) so the qty-drift
                // tolerance compares apples to apples with the internally-tracked base qty.
                var canonicalSym = NormalizeFromOkx(instId.Replace("-SWAP", "", StringComparison.OrdinalIgnoreCase));
                var qtyBase = qty;
                if (IsSwapMode)
                {
                    try
                    {
                        var spec = await GetContractSpecAsync(instId, cancellationToken).ConfigureAwait(false);
                        if (spec.CtVal > 0m) qtyBase = qty * spec.CtVal;
                    }
                    catch { /* fall back to raw contract qty if spec lookup fails */ }
                }
                list.Add(new ExchangePositionSnapshot(canonicalSym, qtyBase, isLong, avg > 0m ? avg : null));
            }
            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX: GetOpenPositionsAsync failed.");
            return Array.Empty<ExchangePositionSnapshot>();
        }
    }

    /// <summary>
    /// Returns the most recent fill (last 5 minutes) for the given symbol — used by
    /// reconciliation ghost-cleanup so we can publish a CLOSED notification with real
    /// exit price + PnL after an OKX-side bracket TP/SL fires asynchronously.
    /// </summary>
    public async Task<ExchangeFillSnapshot?> GetLastFillAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            var instId = ResolveOkxInstrumentId(symbol);
            var instType = IsSwapMode ? "SWAP" : "SPOT";
            var path = $"/api/v5/trade/fills?instType={instType}&instId={Uri.EscapeDataString(instId)}&limit=10";
            var ts = GetIsoTimestamp();
            var sig = ComputeSignature(ts, "GET", path, string.Empty);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
            req.Headers.Add("OK-ACCESS-KEY", ApiKey);
            req.Headers.Add("OK-ACCESS-SIGN", sig);
            req.Headers.Add("OK-ACCESS-TIMESTAMP", ts);
            req.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode) req.Headers.Add("x-simulated-trading", "1");

            var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { TrackHttpFailure(resp); return null; }
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0") return null;
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;

            // OKX returns most recent first. Pick the latest fill within last 5 minutes.
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
            JsonElement? latest = null;
            long latestTs = 0;
            foreach (var f in data.EnumerateArray())
            {
                if (!f.TryGetProperty("ts", out var tsEl)) continue;
                if (!long.TryParse(tsEl.GetString(), out var fillTs)) continue;
                if (fillTs < cutoff) continue;
                if (fillTs > latestTs) { latestTs = fillTs; latest = f; }
            }
            if (latest is null) return null;

            var fillSide = latest.Value.TryGetProperty("side", out var sd) ? sd.GetString() ?? "" : "";
            var fillQty  = ParseDecimal(latest.Value, "fillSz");
            var fillPx   = ParseDecimal(latest.Value, "fillPx");
            var fillFee  = ParseDecimal(latest.Value, "fee");

            // Convert qty contracts → base units when in swap mode, mirroring our open path.
            decimal qtyBase = fillQty;
            if (IsSwapMode)
            {
                try
                {
                    var spec = await GetContractSpecAsync(ResolveOkxInstrumentId(symbol), cancellationToken).ConfigureAwait(false);
                    if (spec.CtVal > 0m) qtyBase = fillQty * spec.CtVal;
                }
                catch { /* keep raw */ }
            }

            return new ExchangeFillSnapshot(
                Symbol: symbol,
                FillSide: fillSide,
                Quantity: qtyBase,
                Price: fillPx,
                FeePaid: Math.Abs(fillFee),
                FillTimeUtc: DateTimeOffset.FromUnixTimeMilliseconds(latestTs).UtcDateTime);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OKX: GetLastFillAsync failed for {Symbol}", symbol);
            return null;
        }
    }

    public void Dispose()
    {
        _hmac?.Dispose();
    }
}
