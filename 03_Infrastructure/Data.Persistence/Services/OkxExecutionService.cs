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
    private record OkxContractSpec(decimal CtVal, decimal LotSz, decimal MinSz, decimal TickSz = 0m);
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

    // [2026-05-13] Hard cap on order-book staleness for execution decisions. When the
    // OKX WebSocket disconnects/silently lags, the cached snapshot can age out → post-only
    // price nudge uses bid/ask from minutes ago → either crosses the new book (51015 reject)
    // or rests far from it (never fills). 30s is comfortably long for normal book updates
    // (typically <1s between snapshots) but short enough to fail closed when the feed dies.
    private const long OrderBookMaxAgeMs = 30_000;

    public bool TryGetLatestOrderBook(string symbol, out OrderBookSnapshot book)
    {
        OrderBookSnapshot candidate = default;
        bool found = false;
        if (_latestOrderBooks.TryGetValue(ResolveOkxInstrumentId(symbol), out var b))
        {
            candidate = b; found = true;
        }
        else if (_latestOrderBooks.TryGetValue(symbol, out b))
        {
            candidate = b; found = true;
        }

        if (!found)
        {
            book = default;
            return false;
        }

        // Staleness gate. WebSocket disconnects can leave a snapshot frozen in the cache;
        // returning it would feed stale bid/ask to ResolvePostOnlyLimitPrice → 51015 reject
        // or queue-deep rest. Better to fail closed: caller falls back to signal.Price (or
        // refuses to submit if RequireOrderBookForPostOnly=true).
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (candidate.TimestampMs > 0 && nowMs - candidate.TimestampMs > OrderBookMaxAgeMs)
        {
            _logger.LogWarning(
                "OKX order-book snapshot for {Symbol} is STALE ({Age}ms > {Max}ms) — refusing to use for execution. WebSocket may be disconnected.",
                symbol, nowMs - candidate.TimestampMs, OrderBookMaxAgeMs);
            book = default;
            return false;
        }

        book = candidate;
        return true;
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

        // [Sprint 1 — Track B, 2026-05-10] Maker-side price nudge.
        // When PostOnlyMode is enabled and the caller passed a Limit signal with a price
        // that would sit inside or cross the spread (typical when the consumer uses mid
        // as signal.Price — e.g. EdgeLabSingleAsset line 242), nudge the price to the
        // outside of the book by MakerEdgeBps so the post_only flag accepts it cleanly.
        // Without this, the demo-account harvest (336 fills, 2026-05-09) measured 0%
        // maker — every order either rejected as 51015 "would_match" or timed out
        // after LimitOrderTimeoutSeconds and fell through to the Market fallback.
        var pxForLimit = signal.Price;
        var postOnlyAttempted = false;
        // Tick size lets the maker price sit INSIDE the spread (queue priority) and keeps every
        // post_only price on the instrument's tick grid.
        var makerTick = (!isMarket && _execOptions.PostOnlyMode && IsSwapMode)
            ? (await GetContractSpecAsync(okxSymbol).ConfigureAwait(false)).TickSz
            : 0m;
        if (!isMarket && _execOptions.PostOnlyMode)
        {
            var (adjusted, source) = ResolvePostOnlyLimitPrice(okxSymbol, signal, _execOptions.MakerEdgeBps,
                makerTick, _execOptions.MakerInsideSpreadTicks);
            if (adjusted is decimal px)
            {
                pxForLimit = px;
                postOnlyAttempted = true;
                _logger.LogInformation(
                    "OKX maker-side price nudge for {Symbol} {Side}: signal.Price={SignalPx} → px={Px} (edge={Edge}bp, src={Src})",
                    okxSymbol, signal.Side, signal.Price, pxForLimit, _execOptions.MakerEdgeBps, source);
            }
            else if (_execOptions.RequireOrderBookForPostOnly)
            {
                _portfolio.ReleaseReservationForOrder(Venue, "USDT", clientOrderId, signal.Quantity * signal.Price);
                _logger.LogWarning(
                    "OKX post_only refused for {Symbol}: no order-book snapshot and RequireOrderBookForPostOnly=true. Caller will retry on next signal cycle.",
                    okxSymbol);
                throw new InvalidOperationException(
                    $"OKX post_only refused for {okxSymbol}: order-book snapshot unavailable.");
            }
            else
            {
                _logger.LogDebug(
                    "OKX maker-side price nudge skipped for {Symbol}: no order-book snapshot. Falling back to signal.Price={SignalPx} (post_only may reject if it crosses).",
                    okxSymbol, signal.Price);
            }
        }

        // [Sprint 1 — Track B] Initial submit + post-only reject retry loop.
        // OKX returns sCode 51015 "Order would match an existing order" when a post_only
        // order would cross the book (e.g. book moved between snapshot read and submit).
        // We widen the maker edge by PostOnlyRejectRetryWideningBps each retry up to
        // PostOnlyRejectRetries attempts. Default: 2 retries → up to 3 attempts at
        // edges {1.0, 1.5, 2.0} bp. Pure post-only failure is logged and propagated;
        // there is NO automatic taker fallback at this stage — the existing
        // LimitOrderTimeoutSeconds / market-fallback path further down handles
        // accepted-but-unfilled orders.
        HttpResponseMessage response;
        string responseJson = string.Empty;
        string? code = null;
        var attempt = 0;
        var maxAttempts = (postOnlyAttempted ? _execOptions.PostOnlyRejectRetries : 0) + 1;
        var currentEdgeBps = _execOptions.MakerEdgeBps;
        while (true)
        {
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
                    // CRITICAL: InvariantCulture — without it Turkish/etc. locales emit "55,57"
                    // for the decimal separator and OKX rejects with sCode=51000 "Parameter px
                    // error". Same applies to sz (already invariant on line 206).
                    px = pxForLimit.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
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

            response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                TrackHttpFailure(response);
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("OKX execution failed: {Error}", error);
                throw new InvalidOperationException($"OKX execution failed: {error}");
            }

            // Check OKX response body for error codes (OKX returns 200 even on logical errors)
            responseJson = await response.Content.ReadAsStringAsync();
            string probedSCode = "";
            using (var probe = JsonDocument.Parse(responseJson))
            {
                code = probe.RootElement.GetProperty("code").GetString();
                if (code != "0")
                {
                    try
                    {
                        var dataArr = probe.RootElement.GetProperty("data");
                        if (dataArr.GetArrayLength() > 0 && dataArr[0].TryGetProperty("sCode", out var sc))
                            probedSCode = sc.GetString() ?? "";
                    }
                    catch { /* ignore */ }
                }
            }
            if (code == "0") break;

            if (postOnlyAttempted && probedSCode == "51015" && attempt < maxAttempts - 1)
            {
                attempt++;
                currentEdgeBps += _execOptions.PostOnlyRejectRetryWideningBps;
                // after a would-match reject, back off behind the touch (no inside-spread step)
                var (retryPx, retrySrc) = ResolvePostOnlyLimitPrice(okxSymbol, signal, currentEdgeBps, makerTick, 0);
                if (retryPx is decimal newPx)
                {
                    pxForLimit = newPx;
                    _logger.LogInformation(
                        "OKX post_only would_match for {Symbol} {Side} attempt {Attempt}/{Max}: widening edge to {Edge}bp, retry px={Px} (src={Src}).",
                        okxSymbol, signal.Side, attempt, maxAttempts, currentEdgeBps, pxForLimit, retrySrc);
                }
                else
                {
                    _logger.LogWarning(
                        "OKX post_only would_match for {Symbol} {Side} attempt {Attempt}/{Max}: no fresh order-book snapshot for retry — reusing prior px={Px}.",
                        okxSymbol, signal.Side, attempt, maxAttempts, pxForLimit);
                }
                continue;
            }

            // Fall through to the existing rejection-handling block (sCode 51169, etc.).
            break;
        }

        using var doc = JsonDocument.Parse(responseJson);
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
        var fillOutcome = FillPollOutcome.Live;

        decimal fillFeeSigned = 0m;
        if (!string.IsNullOrEmpty(ordId))
        {
            // 2026-05-11: same unit-fix as the timeout-wait below — pass orderSz (contracts)
            // so RemainingQty is in contract units that the lot-size guard understands.
            var initial = await PollFillStatusAsync(ordId, okxSymbol, orderSz);
            filledQty = initial.FilledQty;
            remainingQty = initial.RemainingQty;
            fillStatus = initial.Status;
            fillFeeSigned = initial.FillFeeSigned;
            fillOutcome = initial.Outcome;
        }

        // --- Limit-order timeout → Market fallback -----------------------------------
        // Applies only when the original order was a limit and the operator opted in
        // via OrderExecution:LimitOrderTimeoutSeconds. Partial fills are accepted as-is
        // and we only re-submit the *remaining* quantity as a market order.
        //
        // CRITICAL: If the fill-poll outcome is Unknown (every retry failed), we DO NOT
        // run the cancel+market-fallback path. The original order may have silently
        // filled on the venue while our poll was failing — submitting a fresh market
        // order would create a duplicate fill and a runaway position. Reconciliation
        // tick will discover the real OKX state next cycle and sync.
        if (!isMarket
            && fillStatus == TradeStatus.Pending
            && fillOutcome != FillPollOutcome.Unknown
            && !string.IsNullOrEmpty(ordId)
            && _execOptions.LimitOrderTimeoutSeconds > 0)
        {
            // 2026-05-11: pass orderSz (CONTRACTS for SWAP) — not signal.Quantity (base) —
            // so RemainingQty stays in contract units. Prior bug: base/contract unit mix
            // produced a fractional remainder that OKX rejected with sCode 51121 on the
            // market fallback (lot-size violation). orderSz was already lotSz-rounded at
            // submit time; remainders are therefore lot-multiples by construction.
            var waited = await WaitForLimitFillOrTimeoutAsync(ordId, okxSymbol, orderSz);
            filledQty = waited.FilledQty;
            remainingQty = waited.RemainingQty;
            fillStatus = waited.Status;
            fillFeeSigned += waited.FillFeeSigned;
            fillOutcome = waited.Outcome;

            if (fillStatus == TradeStatus.Pending
                && remainingQty > 0m
                && fillOutcome != FillPollOutcome.Unknown)
            {
                _logger.LogWarning(
                    "OKX: limit order {OrdId} for {Symbol} did not fill within {Timeout}s — cancelling and re-submitting remaining {Qty} as MARKET.",
                    ordId, okxSymbol, _execOptions.LimitOrderTimeoutSeconds, remainingQty);

                // Venue-cancelled orders (post_only auto-cancel) need no cancel round-trip: a second
                // cancel fails ("order does not exist") and used to skip the market fallback.
                var venueCancelled = fillOutcome == FillPollOutcome.Canceled;
                var cancelled = venueCancelled || await TryCancelOrderAsync(okxSymbol, ordId);
                if (cancelled && !venueCancelled && _execOptions.CancelVerifyPollAttempts > 0)
                {
                    // [2026-05-13 cancel-verify FSM] Cancel HTTP 200 ≠ cancel applied.
                    // Without this verification, demo congestion (or rare live race) leaves
                    // the limit ALIVE while the market fallback fires → 2× position duplicate.
                    var verify = await VerifyCancelAppliedAsync(okxSymbol, ordId).ConfigureAwait(false);
                    switch (verify.Outcome)
                    {
                        case CancelVerifyOutcome.Canceled:
                            // ✅ proceed to market fallback below.
                            // verify.FilledQty captures any partial that landed pre-cancel —
                            // adopt it so the remainder we send to market is accurate.
                            // [2026-05-14 BUG #4 FIX] verify.FillFeeSigned is the CUMULATIVE
                            // fee for the SAME order (not delta). Earlier waited.FillFeeSigned
                            // also reported cumulative for the same ordId. Adding both would
                            // double-count fees by the partial-fill amount. Replace, not add.
                            if (verify.FilledQty > filledQty)
                            {
                                var deltaFill = verify.FilledQty - filledQty;
                                filledQty = verify.FilledQty;
                                fillFeeSigned = verify.FillFeeSigned;   // not +=  (cumulative)
                                remainingQty = Math.Max(0m, remainingQty - deltaFill);
                            }
                            break;

                        case CancelVerifyOutcome.FilledInRace:
                            // Limit filled during the cancel race. Adopt the fill, skip market.
                            // [2026-05-14 BUG #4 FIX] Same-order cumulative fee — replace, not add.
                            _logger.LogWarning(
                                "OKX: limit order {OrdId} {Symbol} filled IN RACE during cancel ({Filled} qty, fee {Fee}); SKIPPING market fallback to prevent duplicate.",
                                ordId, okxSymbol, verify.FilledQty, verify.FillFeeSigned);
                            filledQty = Math.Max(filledQty, verify.FilledQty);
                            fillFeeSigned = verify.FillFeeSigned;   // not +=  (cumulative for same order)
                            remainingQty = 0m;
                            fillStatus = filledQty > 0m ? TradeStatus.Executed : TradeStatus.Pending;
                            fillOutcome = FillPollOutcome.Filled;
                            cancelled = false; // bypass market submit below
                            break;

                        case CancelVerifyOutcome.StillLive:
                        case CancelVerifyOutcome.Unknown:
                            // Cancel did not apply OR we cannot tell. Either way, market submit
                            // would risk a duplicate-order disaster. Abort. Reconcile loop +
                            // operator UI check will resolve. (Detailed alert already logged
                            // inside VerifyCancelAppliedAsync.)
                            cancelled = false; // bypass market submit below
                            break;
                    }
                }
                if (cancelled)
                {
                    // 2026-05-11: Normalize the remainder to contract spec BEFORE submitting
                    // the market fallback. Previously the leftover from a partial maker fill
                    // was passed through as-is and OKX rejected with sCode 51121 ("Order
                    // quantity must be a multiple of the lot size") because the divisor mix
                    // of base-asset signal.Quantity and contract-unit poll responses left
                    // a fractional remainder (e.g. 853.333 contracts for ADA-USDT-SWAP).
                    // Dust below one lotSz is dropped — partial-fill survival is acceptable;
                    // the worker HoldOpen tick will manage the smaller-than-requested position.
                    decimal mktQty = remainingQty;
                    if (IsSwapMode)
                    {
                        var spec = await GetContractSpecAsync(okxSymbol).ConfigureAwait(false);
                        if (spec.LotSz > 0m)
                        {
                            mktQty = Math.Floor(remainingQty / spec.LotSz) * spec.LotSz;
                        }
                        if (mktQty < spec.MinSz)
                        {
                            _logger.LogWarning(
                                "OKX: market fallback for {Symbol} SKIPPED — remainder {Rem} < minSz {Min} after lotSz {Lot} normalization. Accepting partial maker fill {Filled} as final.",
                                okxSymbol, remainingQty, spec.MinSz, spec.LotSz, filledQty);
                            // Treat as fully resolved: no further submission, status reflects what we got.
                            remainingQty = 0m;
                            fillStatus = filledQty > 0m ? TradeStatus.Executed : TradeStatus.Pending;
                            fillOutcome = FillPollOutcome.Live;
                            mktQty = 0m;
                        }
                    }
                    if (mktQty > 0m)
                    {
                        // [2026-05-14 Day 5 — B1 smart routing] Try IOC first when enabled.
                        // IOC (ordType="ioc") cap-fills at an aggressive cross-side limit,
                        // cancels remainder. Saves 2-5bp slippage vs naive market on volatile
                        // books. If IOC fully fills, skip market entirely. Otherwise reduce
                        // mktQty by IOC partial and proceed to market for the rest.
                        if (_execOptions.SmartRoutingIocEnabled)
                        {
                            var iocResult = await SubmitIocFallbackAsync(signal, okxSymbol, mktQty, tdMode, posSide, reduceOnlyField);
                            // [2026-05-14 BUG #2 FIX caller-side] If IOC outcome is Unknown
                            // (HTTP error / silent submit), the order MAY exist on OKX. Going
                            // straight to MARKET would risk a duplicate. Skip the entire
                            // fallback and let reconcile loop sync.
                            if (iocResult.Outcome == FillPollOutcome.Unknown)
                            {
                                _logger.LogError(
                                    "OKX IOC outcome UNKNOWN for {Symbol} — SKIPPING market fallback to prevent duplicate. Reconcile will sync.",
                                    okxSymbol);
                                fillStatus = filledQty > 0m ? TradeStatus.Executed : TradeStatus.Pending;
                                fillOutcome = FillPollOutcome.Unknown;
                                mktQty = 0m;
                            }
                            else if (iocResult.FilledQty > 0m)
                            {
                                filledQty += iocResult.FilledQty;
                                fillFeeSigned += iocResult.FillFeeSigned;
                                remainingQty -= iocResult.FilledQty;
                                mktQty = Math.Max(0m, mktQty - iocResult.FilledQty);
                                _logger.LogInformation(
                                    "OKX smart-routing IOC filled {Filled}/{Req} for {Symbol}; remaining {Rem} → MARKET fallback.",
                                    iocResult.FilledQty, mktQty + iocResult.FilledQty, okxSymbol, mktQty);
                            }
                            // If mktQty now < minSz after IOC partial, skip MARKET (dust).
                            if (mktQty > 0m && IsSwapMode)
                            {
                                var spec2 = await GetContractSpecAsync(okxSymbol).ConfigureAwait(false);
                                if (mktQty < spec2.MinSz)
                                {
                                    fillStatus = filledQty > 0m ? TradeStatus.Executed : TradeStatus.Pending;
                                    fillOutcome = FillPollOutcome.Filled;
                                    remainingQty = 0m;
                                    mktQty = 0m;
                                }
                            }
                        }

                        if (mktQty <= 0m)
                        {
                            // IOC fully covered the remainder — done.
                            fillStatus = filledQty > 0m ? TradeStatus.Executed : TradeStatus.Pending;
                            fillOutcome = FillPollOutcome.Filled;
                        }
                        else
                        {
                            var mktResult = await SubmitMarketFallbackAsync(signal, okxSymbol, mktQty, tdMode, posSide, reduceOnlyField);
                            filledQty += mktResult.FilledQty;
                            remainingQty = mktResult.RemainingQty;
                            fillStatus = mktResult.Status;
                            fillFeeSigned += mktResult.FillFeeSigned;
                            fillOutcome = mktResult.Outcome;
                        }
                    }
                }
            }
            else if (fillOutcome == FillPollOutcome.Unknown)
            {
                _logger.LogError(
                    "OKX: fill state UNKNOWN for limit order {OrdId} {Symbol} after timeout — skipping market fallback to avoid duplicate fill. Reconcile loop will sync.",
                    ordId, okxSymbol);
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

        // [Sprint 3 — 2026-05-10] Critical attribution bug fix.
        // Pre-fix: Trade was returned without StrategyTag/SignalPrice/ExecutionSource
        // populated, so every fill landed with default StrategyTag="TrendConfluence" in
        // the database — collapsing all 11 EdgeLab strategies into one bucket and
        // making per-strategy P&L attribution impossible. With per-strategy basis
        // weights now in place (Sprint 2), this propagation is mandatory: downstream
        // services (TCA, ShadowAudit, PromotionGate, daily digest) all key on
        // Trade.StrategyTag.
        // Post-fix: copy the signal's identifying fields onto the Trade. Also captures
        // BacktestExpectedPnL when present in metadata so Sprint 4's divergence audit
        // has the realized-vs-expected PnL paired at fill time.
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            SignalPrice = signal.Price,
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
            ExecutionSource = IsDemoMode ? "PAPER" : "LIVE",
            Leverage = leverage,
            MarginMode = marginMode,
            TotalFeesPaid = feePaid,
            NetFunding = 0m,
            StrategyTag = string.IsNullOrWhiteSpace(signal.StrategyTag)
                ? "TrendConfluence"
                : signal.StrategyTag,
        };

        // Capture optional Quantlab signal expectation if the caller propagated it.
        if (signal.Metadata.TryGetValue("BacktestExpectedPnL", out var expectedRaw)
            && decimal.TryParse(expectedRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var expected))
        {
            trade.BacktestExpectedPnL = expected;
        }

        return trade;
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
    /// [Sprint 1 — Track B, 2026-05-10] Compute the post_only-safe limit price.
    /// Uses the cached top-of-book snapshot (populated by the websocket subscriber)
    /// and pulls the price <paramref name="edgeBps"/> bp inside the maker side.
    ///
    ///   BUY  → bestBid × (1 − edgeBps/10000)  (sit BELOW the highest bid)
    ///   SELL → bestAsk × (1 + edgeBps/10000)  (sit ABOVE the lowest ask)
    ///
    /// Both placements guarantee the post_only flag accepts the order: the BUY
    /// cannot match any seller (it's below all asks AND below the best bid), the
    /// SELL cannot match any buyer (it's above all bids AND above the best ask).
    ///
    /// Returns (null, "no-book") when no snapshot is available — caller decides
    /// whether to fall back to <c>signal.Price</c> or refuse to submit (per
    /// <see cref="OrderExecutionOptions.RequireOrderBookForPostOnly"/>).
    /// </summary>
    internal (decimal? Price, string Source) ResolvePostOnlyLimitPrice(
        string okxSymbol, Signal signal, decimal edgeBps, decimal tickSz = 0m, int insideSpreadTicks = 0)
    {
        if (!TryGetLatestOrderBook(okxSymbol, out var book) &&
            !TryGetLatestOrderBook(signal.Symbol, out book))
        {
            return (null, "no-book");
        }

        return ComputeMakerLimitPrice(book, signal.Side, edgeBps, tickSz, insideSpreadTicks);
    }

    /// <summary>
    /// Maker price with queue priority: when the spread is at least 2 ticks wide, step
    /// <paramref name="insideSpreadTicks"/> ticks INSIDE the spread from our side (never reaching
    /// the opposite touch, so post_only still rests); otherwise fall back to
    /// <see cref="ComputePostOnlyLimitPrice"/> (join / back off by edgeBps). With a known tick the
    /// result is snapped to the tick grid away from the opposite side (buy floor, sell ceil).
    /// </summary>
    internal static (decimal? Price, string Source) ComputeMakerLimitPrice(
        OrderBookSnapshot book, TradeSide side, decimal edgeBps, decimal tickSz, int insideSpreadTicks)
    {
        var (basePx, source) = ComputePostOnlyLimitPrice(book, side, edgeBps);
        if (basePx is not decimal px || tickSz <= 0m)
            return (basePx, source);

        var bestBid = book.Bids[0].Price;
        var bestAsk = book.Asks[0].Price;
        var spreadTicks = (int)Math.Floor((bestAsk - bestBid) / tickSz + 1e-9m);
        if (insideSpreadTicks > 0 && spreadTicks >= 2)
        {
            var step = Math.Min(insideSpreadTicks, spreadTicks - 1) * tickSz;
            px = side == TradeSide.Buy ? bestBid + step : bestAsk - step;
            source += $" inside={step / tickSz}t spread={spreadTicks}t";
        }
        px = side == TradeSide.Buy
            ? Math.Floor(px / tickSz) * tickSz
            : Math.Ceiling(px / tickSz) * tickSz;
        return (px, source + $" tick={tickSz.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Pure-function variant of <see cref="ResolvePostOnlyLimitPrice"/> for direct
    /// unit testing (no order-book cache lookup). See that overload for the math.
    /// </summary>
    internal static (decimal? Price, string Source) ComputePostOnlyLimitPrice(
        OrderBookSnapshot book, TradeSide side, decimal edgeBps)
    {
        if (book.Bids is null || book.Asks is null ||
            book.Bids.Length == 0 || book.Asks.Length == 0)
        {
            return (null, "empty-book");
        }

        var bestBid = book.Bids[0].Price;
        var bestAsk = book.Asks[0].Price;
        if (bestBid <= 0m || bestAsk <= 0m || bestAsk < bestBid)
        {
            return (null, "invalid-quote");
        }

        var nudgedEdge = edgeBps > 0m ? edgeBps / 10000m : 0m;
        decimal px = side == TradeSide.Buy
            ? bestBid * (1m - nudgedEdge)
            : bestAsk * (1m + nudgedEdge);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return (px, $"book bid={bestBid.ToString("F8", inv)} ask={bestAsk.ToString("F8", inv)} edge={edgeBps.ToString(inv)}bp");
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
    /// Distinguishes "we know the order is alive on OKX with no fills yet" (Live) from
    /// "we asked OKX 3 times and got no usable answer" (Unknown). Without this distinction,
    /// callers treat both as Pending and may submit a duplicate market fallback while the
    /// original limit is silently filling on the venue — classic ghost-fill double-spend.
    /// </summary>
    // Canceled = the venue cancelled the order (e.g. a post_only that would have crossed is
    // auto-cancelled by OKX). Safe for the market fallback without another cancel round-trip.
    private enum FillPollOutcome { Filled, Live, Unknown, Canceled }

    private readonly record struct FillPollResult(
        decimal FilledQty,
        decimal RemainingQty,
        TradeStatus Status,
        decimal FillFeeSigned,
        FillPollOutcome Outcome);

    /// <summary>
    /// Polls OKX GET /api/v5/trade/order for fill status. Max 3 attempts with 500ms delay.
    /// Outcome semantics:
    ///   Filled  — venue confirmed fully or partially filled; FilledQty / FillFeeSigned reflect reality.
    ///   Live    — venue confirmed the order is on the book with no fill yet; safe to wait/cancel/replace.
    ///   Unknown — every poll attempt failed (HTTP error / timeout / parse). Caller MUST NOT submit a
    ///             market fallback — the order may have filled silently. Reconcile loop will resolve.
    /// </summary>
    private async Task<FillPollResult> PollFillStatusAsync(
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
                    _logger.LogError("OKX fill poll HTTP {Status} for ordId={OrdId} (attempt {Attempt}/{Max})", response.StatusCode, ordId, attempt, maxAttempts);
                    if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0)
                {
                    // OKX returned empty — order not yet visible. Retry; if all retries
                    // come back empty we treat as Unknown (caller should not act).
                    if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var order = data[0];
                var fillSz = ParseDecimal(order, "fillSz");
                var fillFeeSigned = ParseDecimal(order, "fillFee");
                var state  = order.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "live";

                return state switch
                {
                    "filled"           => new FillPollResult(fillSz, 0m, TradeStatus.Executed, fillFeeSigned, FillPollOutcome.Filled),
                    "partially_filled" => new FillPollResult(fillSz, requestedQty - fillSz, TradeStatus.Pending, fillFeeSigned, FillPollOutcome.Filled),
                    "canceled" or "mmp_canceled"
                                       => new FillPollResult(fillSz, requestedQty - fillSz, TradeStatus.Pending, fillFeeSigned, FillPollOutcome.Canceled),
                    _                  => new FillPollResult(0m, requestedQty, TradeStatus.Pending, 0m, FillPollOutcome.Live)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OKX fill poll exception on attempt {Attempt}/{Max} for ordId={OrdId}", attempt, maxAttempts, ordId);
                if (attempt < maxAttempts) await Task.Delay(delayMs, cancellationToken);
            }
        }

        // All 3 attempts failed (HTTP/parse/empty). Return Unknown so the caller refuses
        // to submit a duplicate market fallback. Reconciliation will sync next tick.
        _logger.LogWarning("OKX fill poll EXHAUSTED for ordId={OrdId} on {Inst} — outcome=Unknown; caller will skip fallback, reconcile will resolve.", ordId, instId);
        return new FillPollResult(0m, requestedQty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);
    }

    /// <summary>
    /// Polls a limit order's fill state until either it fills or the configured
    /// <see cref="OrderExecutionOptions.LimitOrderTimeoutSeconds"/> budget is exhausted.
    /// Uses <see cref="OrderExecutionOptions.LimitFillPollIntervalMs"/> between samples.
    /// </summary>
    private async Task<FillPollResult> WaitForLimitFillOrTimeoutAsync(
        string ordId,
        string instId,
        decimal requestedQty,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_execOptions.LimitOrderTimeoutSeconds);
        var pollMs = _execOptions.LimitFillPollIntervalMs;
        var last = new FillPollResult(0m, requestedQty, TradeStatus.Pending, 0m, FillPollOutcome.Live);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            last = await PollFillStatusAsync(ordId, instId, requestedQty, cancellationToken).ConfigureAwait(false);
            if (last.Status == TradeStatus.Executed || last.RemainingQty <= 0m) break;
            // If poll repeatedly comes back Unknown the venue is unreachable — exit early
            // and surface the Unknown outcome so the caller refuses to act.
            if (last.Outcome == FillPollOutcome.Unknown) break;
            if (last.Outcome == FillPollOutcome.Canceled) break;   // venue already cancelled (e.g. post_only would cross)
            try { await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return last;
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
            if (code != "0")
            {
                // [2026-05-15 BUG FIX] Was silently `return false` here, eating the
                // diagnosis. OKX often returns code="1" + data[0].sCode for cancel
                // failures (51400 "Order does not exist" → likely raced-fill, 51401
                // "Already canceled" → no-op, 50113 "Invalid sign" → auth bug, etc.).
                // Surface the detail so future cancel anomalies are diagnosable.
                var rootMsg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "<no msg>";
                string detail = "<no detail>";
                try
                {
                    if (doc.RootElement.TryGetProperty("data", out var d) && d.GetArrayLength() > 0)
                    {
                        var d0 = d[0];
                        var sc = d0.TryGetProperty("sCode", out var sce) ? sce.GetString() : "";
                        var sm = d0.TryGetProperty("sMsg", out var sme) ? sme.GetString() : "";
                        detail = $"sCode={sc} sMsg={sm}";
                    }
                }
                catch { }
                _logger.LogWarning(
                    "OKX cancel-order returned code={Code} msg='{Msg}' | {Detail} | ordId={OrdId} body={Body}",
                    code, rootMsg, detail, ordId, json);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX: exception while cancelling order {OrdId}.", ordId);
            return false;
        }
    }

    /// <summary>
    /// [2026-05-13 cancel-verify FSM] OKX cancel-order HTTP 200 ≠ cancel applied on the venue.
    /// On demo congestion (or rare live race) the cancel can lag or be silently dropped, so
    /// the previous limit stays "live" while the caller submits a market fallback — producing
    /// a duplicate-order disaster (both limit AND market alive simultaneously → 2× position).
    /// This method polls GET /trade/order until the state confirms one of the terminal
    /// outcomes below. Caller MUST check <see cref="CancelVerifyOutcome"/> before submitting
    /// any fallback.
    /// </summary>
    private enum CancelVerifyOutcome
    {
        /// <summary>state=canceled or mmp_canceled — safe to submit market fallback for the remainder.</summary>
        Canceled,
        /// <summary>state=filled or partially_filled — limit filled during the cancel race; adopt the fill, DO NOT submit fallback.</summary>
        FilledInRace,
        /// <summary>state=live after all retries — cancel did not apply OKX-side. ABORT fallback to prevent duplicate; reconcile loop will sync.</summary>
        StillLive,
        /// <summary>Polling failed (HTTP error / parse / empty). ABORT fallback; reconcile loop will sync.</summary>
        Unknown
    }

    private readonly record struct CancelVerifyResult(
        CancelVerifyOutcome Outcome,
        decimal FilledQty,
        decimal FillFeeSigned);

    private async Task<CancelVerifyResult> VerifyCancelAppliedAsync(
        string instId,
        string ordId,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(1, _execOptions.CancelVerifyPollAttempts);
        var delayMs = Math.Clamp(_execOptions.CancelVerifyPollDelayMs, 100, 5000);

        for (int attempt = 1; attempt <= attempts; attempt++)
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

                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    TrackHttpFailure(response);
                    _logger.LogWarning("OKX cancel-verify HTTP {Status} for ordId={OrdId} (attempt {Attempt}/{Max})",
                        response.StatusCode, ordId, attempt, attempts);
                    if (attempt < attempts) await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0)
                {
                    if (attempt < attempts) await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var order = data[0];
                var state = order.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "live";
                var fillSz = ParseDecimal(order, "fillSz");
                var fillFee = ParseDecimal(order, "fillFee");

                switch (state)
                {
                    case "canceled":
                    case "mmp_canceled":
                        // ✅ confirmed. fillSz captures any partial-fill that landed before cancel applied.
                        return new CancelVerifyResult(CancelVerifyOutcome.Canceled, fillSz, fillFee);

                    case "filled":
                    case "partially_filled":
                        // Limit filled during the cancel race. OKX's cancel-order endpoint cancels
                        // the unfilled portion automatically when called against a partially-filled
                        // order, so partial_filled here means "remaining was canceled, what's filled
                        // is what we got". Either way, do NOT submit fallback — adopt the fill.
                        return new CancelVerifyResult(CancelVerifyOutcome.FilledInRace, fillSz, fillFee);

                    case "live":
                    default:
                        // Cancel HTTP succeeded but order still on the book. Wait + retry.
                        if (attempt < attempts) await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OKX cancel-verify exception attempt {Attempt}/{Max} for ordId={OrdId}", attempt, attempts, ordId);
                if (attempt < attempts) await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        // Exhausted. Either polls failed entirely OR state remained live across all attempts.
        // Caller MUST treat this as "do not submit fallback" — duplicate-order prevention
        // is more important than getting a fill on this cycle.
        _logger.LogError(
            "OKX_ORPHAN_SUSPECT: cancel-verify EXHAUSTED for ordId={OrdId} on {Inst} after {Attempts} attempts — " +
            "order may still be live on OKX. ABORTING market fallback to prevent duplicate position. " +
            "Reconcile loop will sync; operator should verify on OKX UI.",
            ordId, instId, attempts);
        return new CancelVerifyResult(CancelVerifyOutcome.StillLive, 0m, 0m);
    }

    /// <summary>
    /// [2026-05-14 Day 5 — B1 smart routing] IOC limit fallback: aggressive cross-side
    /// limit price with Immediate-Or-Cancel — fills what it can within ~1s and cancels
    /// the rest. Trades 2-5bp slippage savings vs naive market when book is stable; if
    /// book is moving fast and IOC partial-fills, caller falls through to MARKET for the
    /// remainder. Uses bid/ask from the cached order-book snapshot (already age-gated by
    /// TryGetLatestOrderBook). When no book is available, returns FilledQty=0 so caller
    /// proceeds straight to MARKET.
    /// </summary>
    private async Task<FillPollResult> SubmitIocFallbackAsync(
        Signal original,
        string okxSymbol,
        decimal qty,
        string tdMode,
        string? posSide,
        bool? reduceOnlyField,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Compute aggressive cross-side limit price from latest book.
            if (!TryGetLatestOrderBook(okxSymbol, out var book) || book.Bids.Length == 0 || book.Asks.Length == 0)
            {
                _logger.LogDebug("OKX IOC fallback: no order-book snapshot for {Sym} — caller will use MARKET.", okxSymbol);
                return new FillPollResult(0m, qty, TradeStatus.Pending, 0m, FillPollOutcome.Live);
            }

            var bestBid = book.Bids[0].Price;
            var bestAsk = book.Asks[0].Price;
            // [2026-05-14 BUG #3 FIX] Guard against zero/negative quotes — corrupt L2 data
            // could otherwise emit a 0-price IOC that OKX rejects (or worse, fills cheaply).
            if (bestBid <= 0m || bestAsk <= 0m)
            {
                _logger.LogWarning(
                    "OKX IOC fallback: degenerate book for {Sym} (bid={Bid} ask={Ask}) — caller will use MARKET.",
                    okxSymbol, bestBid, bestAsk);
                return new FillPollResult(0m, qty, TradeStatus.Pending, 0m, FillPollOutcome.Live);
            }

            var offsetBps = _execOptions.SmartRoutingIocPriceOffsetBps;
            decimal iocPrice = original.Side == TradeSide.Buy
                ? bestAsk * (1m + offsetBps / 10000m)   // pay up to ask+offset
                : bestBid * (1m - offsetBps / 10000m);  // hit down to bid-offset

            // Build clOrdId — 32-char limit, "I" suffix to differentiate from "M" market.
            var baseClientId = ExecutionOrderIdentity.EnsureClientOrderId(original);
            var truncated = baseClientId.Length > 31 ? baseClientId.Substring(0, 31) : baseClientId;
            var iocClientId = truncated + "I";

            var body = JsonSerializer.Serialize(new
            {
                instId = okxSymbol,
                tdMode,
                side = original.Side == TradeSide.Buy ? "buy" : "sell",
                ordType = "ioc",                // immediate or cancel — taker with price protection
                clOrdId = iocClientId,
                px = iocPrice.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
                sz = qty.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
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
                // [2026-05-14 BUG #2 FIX] Was returning Live (caller proceeds to MARKET = potential
                // duplicate if IOC actually placed silently). HTTP failure after POST means we don't
                // know the order state. Return Unknown so caller skips MARKET and reconcile syncs.
                _logger.LogError("OKX IOC fallback HTTP {Status} — outcome UNKNOWN, caller will SKIP market fallback to prevent duplicate.", resp.StatusCode);
                return new FillPollResult(0m, qty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                // Explicit OKX rejection — order definitely NOT placed. Caller proceeds to MARKET.
                _logger.LogWarning("OKX IOC fallback rejected: body={Body} — caller will fall through to MARKET.", json);
                return new FillPollResult(0m, qty, TradeStatus.Pending, 0m, FillPollOutcome.Live);
            }

            var iocOrdId = doc.RootElement.GetProperty("data")[0].GetProperty("ordId").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(iocOrdId))
            {
                // Code=0 (success) but no ordId returned — anomalous OKX response. Order MAY exist.
                // Treat as Unknown to prevent duplicate.
                _logger.LogError("OKX IOC fallback: code=0 but empty ordId — outcome UNKNOWN, caller will SKIP market.");
                return new FillPollResult(0m, qty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);
            }

            // IOC fills (or cancels remainder) within ~1s. Poll once with short timeout.
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            var pollResult = await PollFillStatusAsync(iocOrdId, okxSymbol, qty, cancellationToken).ConfigureAwait(false);
            return pollResult;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // [2026-05-14 BUG #2 FIX] Exception after submit is the same uncertainty as HTTP error.
            // Return Unknown so caller skips MARKET fallback.
            _logger.LogError(ex, "OKX IOC fallback exception — outcome UNKNOWN, caller will SKIP market fallback.");
            return new FillPollResult(0m, qty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);
        }
    }

    /// <summary>
    /// [2026-05-13 market-retry] Wraps the inner submit with exponential-backoff retry
    /// for transient OKX rejects (Systems busy / market volatility / sCode 51008 margin
    /// recompute race). Outcome=Unknown (HTTP error / no ordId) does NOT retry — the
    /// order may have filled silently; reconcile loop resolves. Outcome=Filled returns
    /// immediately. Only Outcome=Live (explicit rejection from OKX with code!="0")
    /// triggers a retry. After all attempts exhausted, returns Live with a hard alert log.
    /// </summary>
    private async Task<FillPollResult> SubmitMarketFallbackAsync(
        Signal original,
        string okxSymbol,
        decimal remainingQty,
        string tdMode,
        string? posSide,
        bool? reduceOnlyField,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(1, _execOptions.MarketFallbackRetryAttempts);
        FillPollResult last = new FillPollResult(0m, remainingQty, TradeStatus.Pending, 0m, FillPollOutcome.Live);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            last = await TrySubmitMarketFallbackOnceAsync(
                original, okxSymbol, remainingQty, tdMode, posSide, reduceOnlyField, attempt, cancellationToken
            ).ConfigureAwait(false);

            // Terminal outcomes: Filled (success) or Unknown (HTTP/parse error — must NOT retry
            // because order may have placed silently and retry would double-fill).
            if (last.Outcome == FillPollOutcome.Filled || last.Outcome == FillPollOutcome.Unknown)
                return last;

            // Outcome=Live with attempts remaining → exponential backoff retry.
            if (attempt < attempts)
            {
                var backoffMs = 500 * (1 << (attempt - 1));   // 500ms, 1000ms, 2000ms, 4000ms ...
                _logger.LogWarning(
                    "OKX market fallback rejected (attempt {Attempt}/{Max}) for {Symbol} qty={Qty}; retrying in {Ms}ms.",
                    attempt, attempts, okxSymbol, remainingQty, backoffMs);
                try { await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }

        _logger.LogError(
            "OKX_MARKET_FALLBACK_FAILED: all {Attempts} attempts exhausted for {Symbol} qty={Qty}. " +
            "Position NOT opened/closed via fallback path. Strategy will retry next cycle.",
            attempts, okxSymbol, remainingQty);
        return last;
    }

    private async Task<FillPollResult> TrySubmitMarketFallbackOnceAsync(
        Signal original,
        string okxSymbol,
        decimal remainingQty,
        string tdMode,
        string? posSide,
        bool? reduceOnlyField,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            // OKX clOrdId hard limit: 32 chars. Original Guid-based id is exactly 32, so the
            // legacy "+M" suffix overflows to 33 → sCode 51000 "Parameter clOrdId error" →
            // every market fallback silently rejected. Truncate to 31 first, then suffix.
            // 2026-04-30: this was the root cause of the "OKX-rejects-everything" symptom
            // surfaced by the phantom-open guard.
            // 2026-05-13: when retrying we MUST vary the clOrdId — OKX rejects duplicate
            // clOrdId with sCode 51075. Append the attempt number after "M" within the 32-char
            // budget. Attempt 1 stays as "...M" (legacy unchanged) so existing reconcile keeps
            // matching; attempts ≥2 use "...Mn" where n is the attempt index.
            var baseClientId = ExecutionOrderIdentity.EnsureClientOrderId(original);
            var truncated = baseClientId.Length > 31 ? baseClientId.Substring(0, 31) : baseClientId;
            var suffix = attemptNumber <= 1 ? "M" : $"M{attemptNumber}";
            // suffix may be up to 3 chars ("M99") — drop one more char from truncated if needed.
            if (truncated.Length + suffix.Length > 32)
                truncated = truncated.Substring(0, 32 - suffix.Length);
            var fallbackClientId = truncated + suffix;
            var body = JsonSerializer.Serialize(new
            {
                instId = okxSymbol,
                tdMode,
                side = original.Side == TradeSide.Buy ? "buy" : "sell",
                ordType = "market",
                clOrdId = fallbackClientId,
                sz = remainingQty.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
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
                _logger.LogError("OKX market fallback failed: HTTP {Status} (attempt {Attempt}).", resp.StatusCode, attemptNumber);
                // Unknown — order may have placed silently; do NOT retry to avoid double-fill.
                return new FillPollResult(0m, remainingQty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);
            }
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                // OKX puts the actionable detail in data[0].sCode / sMsg even when the root
                // code is the generic "1 - All operations failed". Surface both so we can
                // distinguish "51008 insufficient margin" from "51010 leverage too high"
                // from "51121 lot size error" — log-only triage from a single field is impossible.
                var rootMsg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "<no msg>";
                string detail = "<no detail>";
                try
                {
                    if (doc.RootElement.TryGetProperty("data", out var d) && d.GetArrayLength() > 0)
                    {
                        var d0 = d[0];
                        var sc = d0.TryGetProperty("sCode", out var sce) ? sce.GetString() : "";
                        var sm = d0.TryGetProperty("sMsg", out var sme) ? sme.GetString() : "";
                        detail = $"sCode={sc} sMsg={sm}";
                    }
                }
                catch { }
                _logger.LogError("OKX market fallback rejected (attempt {Attempt}): {Msg} | {Detail} | body={Body}",
                    attemptNumber, rootMsg, detail, json);
                // Fallback REJECTED — order definitely not placed. Outcome=Live signals the
                // retry wrapper to back off and try again (transient OKX congestion).
                return new FillPollResult(0m, remainingQty, TradeStatus.Pending, 0m, FillPollOutcome.Live);
            }

            var fbOrdId = doc.RootElement.GetProperty("data")[0].GetProperty("ordId").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(fbOrdId))
                return new FillPollResult(0m, remainingQty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);

            return await PollFillStatusAsync(fbOrdId, okxSymbol, remainingQty, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX: exception during market fallback submission (attempt {Attempt}).", attemptNumber);
            return new FillPollResult(0m, remainingQty, TradeStatus.Pending, 0m, FillPollOutcome.Unknown);
        }
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        // 2026-05-11: Removed the "OKX-LIVE-CACHED" early-return.
        //
        // The cached path read _portfolio.GetAvailableBalance(Venue, "USDT") — the very value
        // we are about to seed FROM the result of this call. That made the seeder
        // (PaperTradingHostedService.TrySyncInitialBalanceFromExchangeAsync) re-anchor the
        // portfolio's stale Cash to itself, fabricating a "fresh sync from OKX" log line
        // while never touching the real exchange. Symptom: a stale $96K snapshot survived
        // every restart and over-sized EdgeLab orders by ~48× until the slow periodic
        // ExchangeBalanceSync tick finally hit the REST endpoint minutes later.
        //
        // Always query the venue. The HTTP cost is negligible compared to the risk of
        // sizing orders off a self-referential cache.
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
        decimal usdtEquity = 0;
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() > 0)
        {
            var details = data[0].GetProperty("details");
            foreach (var detail in details.EnumerateArray())
            {
                var ccy = detail.GetProperty("ccy").GetString();
                if (ccy == "USDT")
                {
                    usdtBalance = ParseDecimalStr(detail, "availBal");
                    // `eq` = USDT equity incl. unrealized PnL and margin locked in positions.
                    // availBal drops when margin is locked, so it must never be used as equity
                    // (it made RiskSupervisor read every position open as a drawdown).
                    usdtEquity = ParseDecimalStr(detail, "eq", usdtBalance);
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
            Equity = usdtEquity
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
    // [2026-05-17 critical fix — clock drift] OKX rejects timestamps > 30s off server clock.
    // When Windows W32Time is mis-configured (Source: Local CMOS), drift accumulates freely.
    // ThatsMyQuant.Bot.OkxClockSync periodically fetches OKX server time and exposes an
    // offset-adjusted UtcNow. We read it via reflection-free static accessor — OkxClockSync
    // is in ThatsMyQuant.Bot project which we cannot reference from Infrastructure (would
    // create a circular dep). Fallback: local DateTime.UtcNow.
    private static readonly Func<DateTime>? _clockSourceLookup = ResolveClockSourceLookup();

    private static Func<DateTime>? ResolveClockSourceLookup()
    {
        // Discover ThatsMyQuant.Bot.OkxClockSync.UtcNowOffsetAdjusted at startup. If the
        // type isn't loaded (e.g., unit test harness), fall back to DateTime.UtcNow silently.
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ThatsMyQuant.Bot");
            var type = asm?.GetType("ThatsMyQuant.Bot.OkxClockSync");
            var prop = type?.GetProperty("UtcNowOffsetAdjusted",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop is null) return null;
            return () => (DateTime)prop.GetValue(null)!;
        }
        catch { return null; }
    }

    private static string GetIsoTimestamp()
    {
        var now = _clockSourceLookup is not null ? _clockSourceLookup() : DateTime.UtcNow;
        return now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

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
            var tickSz = ParseDecimalStr(row, "tickSz", 0m);
            var spec = new OkxContractSpec(ctVal, lotSz, minSz, tickSz);
            _logger.LogInformation("OKX contract spec {InstId}: ctVal={CtVal} lotSz={LotSz} minSz={MinSz} tickSz={TickSz}",
                instId, ctVal, lotSz, minSz, tickSz);
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

    /// <summary>
    /// [2026-05-16] Closed-position history for ledger mirror. OKX v5
    /// /api/v5/account/positions-history returns one record per CLOSED position
    /// (open + close fully aggregated), including realizedPnl and fees. We page
    /// up to 100 records per call; for our daily volumes the 3-month default
    /// window comfortably fits inside one page.
    /// </summary>
    public async Task<IReadOnlyList<ClosedPositionSnapshot>> GetClosedPositionsHistoryAsync(
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken = default)
    {
        var instType = IsSwapMode ? "SWAP" : "MARGIN";
        var sinceMs = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var untilMs = new DateTimeOffset(DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        var path = $"/api/v5/account/positions-history?instType={instType}&begin={sinceMs}&end={untilMs}&limit=100";
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
                _logger.LogWarning("OKX positions-history fetch failed: HTTP {Status}", response.StatusCode);
                return Array.Empty<ClosedPositionSnapshot>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                _logger.LogWarning("OKX positions-history non-zero code: {Msg}",
                    doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "<no msg>");
                return Array.Empty<ClosedPositionSnapshot>();
            }

            var data = doc.RootElement.GetProperty("data");
            var list = new List<ClosedPositionSnapshot>(data.GetArrayLength());
            foreach (var p in data.EnumerateArray())
            {
                var instId = p.TryGetProperty("instId", out var iid) ? iid.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(instId)) continue;

                // posSide: long/short/net. For net mode, derive from direction field.
                var posSide = p.TryGetProperty("posSide", out var ps) ? ps.GetString() ?? "net" : "net";
                var direction = p.TryGetProperty("direction", out var dir) ? dir.GetString() ?? "" : "";
                bool wasLong = posSide switch
                {
                    "long" => true,
                    "short" => false,
                    _ => string.Equals(direction, "long", StringComparison.OrdinalIgnoreCase)
                };

                var openMaxPos = ParseDecimal(p, "openMaxPos");   // peak open size in contracts
                var openAvgPx = ParseDecimal(p, "openAvgPx");
                var closeAvgPx = ParseDecimal(p, "closeAvgPx");
                var realizedPnl = ParseDecimal(p, "realizedPnl");
                var fee = ParseDecimal(p, "fee");                 // OKX returns negative for paid fees
                var cTimeMs = p.TryGetProperty("cTime", out var ct) && long.TryParse(ct.GetString(), out var c) ? c : 0L;
                var uTimeMs = p.TryGetProperty("uTime", out var ut) && long.TryParse(ut.GetString(), out var u) ? u : 0L;

                // Skip degenerate records — anything still open or missing required fields.
                if (openMaxPos == 0m || openAvgPx <= 0m || closeAvgPx <= 0m || uTimeMs == 0L) continue;

                // De-contract qty (positions-history returns contracts for SWAP).
                decimal qtyBase = openMaxPos;
                if (IsSwapMode)
                {
                    try
                    {
                        var spec = await GetContractSpecAsync(instId, cancellationToken).ConfigureAwait(false);
                        if (spec.CtVal > 0m) qtyBase = openMaxPos * spec.CtVal;
                    }
                    catch { /* keep raw contract qty */ }
                }

                var canonicalSym = NormalizeFromOkx(instId.Replace("-SWAP", "", StringComparison.OrdinalIgnoreCase));
                list.Add(new ClosedPositionSnapshot(
                    Symbol: canonicalSym,
                    WasLong: wasLong,
                    Quantity: qtyBase,
                    OpenAvgPrice: openAvgPx,
                    CloseAvgPrice: closeAvgPx,
                    OpenTimeUtc: cTimeMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(cTimeMs).UtcDateTime : DateTime.UtcNow,
                    CloseTimeUtc: DateTimeOffset.FromUnixTimeMilliseconds(uTimeMs).UtcDateTime,
                    RealizedPnl: realizedPnl,
                    FeePaid: Math.Abs(fee),
                    StrategyTag: null));   // OKX doesn't surface strategy attribution; left null
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX: GetClosedPositionsHistoryAsync failed.");
            return Array.Empty<ClosedPositionSnapshot>();
        }
    }

    // Positions reads are serialized and spaced (OKX: 10 req / 2 s on /account/positions).
    // 20+ EdgeLab strategies poll at the same second; unthrottled reads hit HTTP 429.
    private readonly SemaphoreSlim _positionsGate = new(1, 1);
    private DateTime _lastPositionsCallUtc = DateTime.MinValue;
    private static readonly TimeSpan PositionsMinSpacing = TimeSpan.FromMilliseconds(250);

    public async Task<IReadOnlyList<ExchangePositionSnapshot>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        => (await TryGetOpenPositionsAsync(cancellationToken).ConfigureAwait(false)).Positions;

    public async Task<(bool Ok, IReadOnlyList<ExchangePositionSnapshot> Positions)> TryGetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        await _positionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = _lastPositionsCallUtc + PositionsMinSpacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                _lastPositionsCallUtc = DateTime.UtcNow;
                var (ok, list, rateLimited) = await FetchOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
                if (ok || !rateLimited || attempt >= 1) return (ok, list);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _positionsGate.Release();
        }
    }

    private async Task<(bool Ok, IReadOnlyList<ExchangePositionSnapshot> Positions, bool RateLimited)> FetchOpenPositionsAsync(
        CancellationToken cancellationToken)
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
                return (false, Array.Empty<ExchangePositionSnapshot>(), (int)response.StatusCode == 429);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetString() != "0")
            {
                _logger.LogWarning("OKX positions returned non-zero code: {Msg}",
                    doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "<no msg>");
                return (false, Array.Empty<ExchangePositionSnapshot>(), false);
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
            return (true, list, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX: GetOpenPositionsAsync failed.");
            return (false, Array.Empty<ExchangePositionSnapshot>(), false);
        }
    }

    /// <summary>
    /// Returns the most recent fill (last 5 minutes) for the given symbol — used by
    /// reconciliation ghost-cleanup so we can publish a CLOSED notification with real
    /// exit price + PnL after an OKX-side bracket TP/SL fires asynchronously.
    /// </summary>
    public async Task<ExchangeFillSnapshot?> GetLastFillAsync(string symbol, int lookbackMinutes = 5, CancellationToken cancellationToken = default)
    {
        try
        {
            var instId = ResolveOkxInstrumentId(symbol);
            var instType = IsSwapMode ? "SWAP" : "SPOT";
            // /api/v5/trade/fills returns the last 3 days max. limit=100 covers all
            // realistic ghost-recovery windows; older fills require fills-history.
            var path = $"/api/v5/trade/fills?instType={instType}&instId={Uri.EscapeDataString(instId)}&limit=100";
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

            // OKX returns most recent first. Pick the latest fill within the requested window.
            var safeMinutes = lookbackMinutes < 1 ? 1 : lookbackMinutes;
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-safeMinutes).ToUnixTimeMilliseconds();
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
            // 2026-05-07 (Sprint 1.2): capture execType ("T"=taker, "M"=maker) for MakerFillRatioMonitor.
            var execType = latest.Value.TryGetProperty("execType", out var et) ? et.GetString() : null;
            // 2026-05-07 (Sprint 1.2 live-fill validation): capture subType (Hedge Mode entry/exit signal) and fillPnl (Net Mode entry/exit signal — realized PnL is non-zero only on close fills).
            var subType = latest.Value.TryGetProperty("subType", out var st) ? st.GetString() : null;
            var realizedPnl = ParseDecimal(latest.Value, "fillPnl");

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
                FillTimeUtc: DateTimeOffset.FromUnixTimeMilliseconds(latestTs).UtcDateTime,
                ExecType: execType,
                SubType: subType,
                RealizedPnl: realizedPnl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OKX: GetLastFillAsync failed for {Symbol}", symbol);
            return null;
        }
    }

    /// <summary>
    /// Pages through OKX <c>/api/v5/trade/fills-history</c> (up to last 3 months) and
    /// returns every fill in [<paramref name="sinceUtc"/>, <paramref name="untilUtc"/>].
    /// Each <see cref="ExchangeFillSnapshot"/> carries the venue-reported FeePaid in quote
    /// currency — the canonical "real fee" used for fee-harvest post-mortems and the
    /// permanent <c>RealFeePaid</c> field on closed paper positions.
    /// </summary>
    public async Task<IReadOnlyList<ExchangeFillSnapshot>> GetFillsHistoryAsync(
        DateTime sinceUtc,
        DateTime untilUtc,
        string? instType = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedInstType = !string.IsNullOrWhiteSpace(instType)
            ? instType!.ToUpperInvariant()
            : (IsSwapMode ? "SWAP" : "SPOT");
        var sinceMs = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var untilMs = new DateTimeOffset(DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        var results = new List<ExchangeFillSnapshot>(256);
        string? afterCursor = null;
        const int hardPageCap = 50; // safety: 50 × 100 = 5000 fills max per call

        for (var page = 0; page < hardPageCap; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"/api/v5/trade/fills-history?instType={resolvedInstType}&limit=100&begin={sinceMs}&end={untilMs}";
            if (!string.IsNullOrEmpty(afterCursor)) path += $"&after={afterCursor}";

            var ts = GetIsoTimestamp();
            var sig = ComputeSignature(ts, "GET", path, string.Empty);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
            req.Headers.Add("OK-ACCESS-KEY", ApiKey);
            req.Headers.Add("OK-ACCESS-SIGN", sig);
            req.Headers.Add("OK-ACCESS-TIMESTAMP", ts);
            req.Headers.Add("OK-ACCESS-PASSPHRASE", Passphrase);
            if (IsDemoMode) req.Headers.Add("x-simulated-trading", "1");

            HttpResponseMessage resp;
            try { resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OKX fills-history page {Page} HTTP failed.", page);
                break;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("OKX fills-history page {Page} HTTP {Status} body={Body}", page, (int)resp.StatusCode, body);
                break;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (code != "0")
            {
                var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "unknown";
                _logger.LogWarning("OKX fills-history error code={Code} msg={Msg}", code, msg);
                break;
            }
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                break;

            string? lastBillId = null;
            foreach (var f in data.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instId = f.TryGetProperty("instId", out var iv) ? iv.GetString() ?? "" : "";
                var fillSide = f.TryGetProperty("side", out var sv) ? sv.GetString() ?? "" : "";
                var fillQty = ParseDecimal(f, "fillSz");
                var fillPx = ParseDecimal(f, "fillPx");
                var fillFee = ParseDecimal(f, "fee"); // OKX returns negative when paid (out-of-pocket)
                var tsMs = f.TryGetProperty("ts", out var tsEl) && long.TryParse(tsEl.GetString(), out var t) ? t : 0L;
                lastBillId = f.TryGetProperty("billId", out var bId) ? bId.GetString() : lastBillId;
                // 2026-05-07 (Sprint 1.2): capture execType ("T"=taker, "M"=maker) for MakerFillRatioMonitor.
                var execType = f.TryGetProperty("execType", out var et) ? et.GetString() : null;
                // 2026-05-07 (Sprint 1.2 live-fill validation): capture subType (Hedge Mode entry/exit) and fillPnl (Net Mode entry/exit). In our Net Mode config OKX returns subType=1/2 (direction only) so the maker-fill monitor falls back to fillPnl != 0 ⇒ exit.
                var subType = f.TryGetProperty("subType", out var st) ? st.GetString() : null;
                var realizedPnl = ParseDecimal(f, "fillPnl");

                // Decontract qty for swaps (mirrors GetLastFillAsync logic).
                decimal qtyBase = fillQty;
                if (resolvedInstType == "SWAP")
                {
                    try
                    {
                        var spec = await GetContractSpecAsync(instId, cancellationToken).ConfigureAwait(false);
                        if (spec.CtVal > 0m) qtyBase = fillQty * spec.CtVal;
                    }
                    catch { /* keep raw */ }
                }

                // Normalize back to internal symbol format (BTC-USDT-SWAP → BTCUSDT).
                var internalSym = instId.Replace("-SWAP", "", StringComparison.OrdinalIgnoreCase).Replace("-", "");

                results.Add(new ExchangeFillSnapshot(
                    Symbol: internalSym,
                    FillSide: fillSide,
                    Quantity: qtyBase,
                    Price: fillPx,
                    FeePaid: Math.Abs(fillFee),
                    FillTimeUtc: DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime,
                    ExecType: execType,
                    SubType: subType,
                    RealizedPnl: realizedPnl));
            }

            // OKX paginates by billId — pass smallest billId of returned page as `after` to fetch older rows.
            if (string.IsNullOrEmpty(lastBillId) || data.GetArrayLength() < 100) break;
            afterCursor = lastBillId;
        }

        return results;
    }

    public void Dispose()
    {
        _hmac?.Dispose();
    }
}
