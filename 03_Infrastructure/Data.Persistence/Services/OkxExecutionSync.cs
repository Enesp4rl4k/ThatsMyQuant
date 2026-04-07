using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Resilience;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Authenticated OKX private execution-report stream.
/// Uses the private orders channel so trade state is updated from venue events instead of REST polling.
/// </summary>
public sealed class OkxExecutionSync : IExchangeExecutionSync
{
    private const string ProductionPrivateWebSocketUrl = "wss://ws.okx.com:8443/ws/v5/private";
    private const string DemoPrivateWebSocketUrl = "wss://wspap.okx.com:8443/ws/v5/private?brokerId=9999";

    private readonly ILogger<OkxExecutionSync> _logger;
    private readonly IConfiguration _configuration;
    private readonly ApiVault? _apiVault;
    private readonly ExecutionReportStream _executionReportStream = new();
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _maxReconnectAttempts;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TaskCompletionSource<bool>? _loginAcknowledgement;
    private TaskCompletionSource<bool>? _subscriptionAcknowledgement;

    public OkxExecutionSync(
        ILogger<OkxExecutionSync> logger,
        IConfiguration configuration,
        ApiVault? apiVault = null,
        TimeSpan? heartbeatInterval = null,
        int maxReconnectAttempts = 10)
    {
        _logger = logger;
        _configuration = configuration;
        _apiVault = apiVault;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(20);
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    public IObservable<ExecutionReport> ExecutionReports => _executionReportStream;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        EnsureCredentialsPresent();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunWithReconnectAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await CloseSocketAsync();

        _runTask = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _executionReportStream.Complete();
        _executionReportStream.Dispose();
    }

    private async Task RunWithReconnectAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectCoreAsync(cancellationToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = BackoffCalculator.Compute(attempt - 1);
                _logger.LogWarning(
                    ex,
                    "OKX execution sync disconnected. Reconnect attempt {Attempt}/{MaxAttempts} in {DelaySeconds:F1}s.",
                    attempt,
                    _maxReconnectAttempts,
                    delay.TotalSeconds);

                await CloseSocketAsync();

                if (attempt >= _maxReconnectAttempts)
                {
                    throw new InvalidOperationException("OKX execution sync exceeded maximum reconnect attempts.", ex);
                }

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await CloseSocketAsync();

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(ResolveWebSocketUrl()), cancellationToken);

        _loginAcknowledgement = NewAcknowledgement();
        _subscriptionAcknowledgement = NewAcknowledgement();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = ReceiveLoopAsync(_webSocket, linkedCts.Token);
        var heartbeatTask = HeartbeatLoopAsync(_webSocket, linkedCts.Token);

        await SendTextAsync(_webSocket, CreateLoginMessage());
        await AwaitAcknowledgementAsync(_loginAcknowledgement, "login", cancellationToken);

        await SendTextAsync(_webSocket, CreateSubscribeMessage());
        await AwaitAcknowledgementAsync(_subscriptionAcknowledgement, "orders subscription", cancellationToken);

        _logger.LogInformation("Connected to OKX private orders WebSocket.");

        try
        {
            await receiveTask;
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                messageBuffer.SetLength(0);
                HandleIncomingMessage(payload);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            await SendTextAsync(socket, "ping");
        }
    }

    private void HandleIncomingMessage(string payload)
    {
        if (string.Equals(payload, "pong", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.TryGetProperty("event", out var eventElement))
        {
            HandleControlEvent(root, eventElement.GetString() ?? string.Empty);
            return;
        }

        if (TryParseExecutionReports(root, out var reports))
        {
            foreach (var report in reports)
            {
                _executionReportStream.Publish(report);
            }
        }
    }

    private void HandleControlEvent(JsonElement root, string eventName)
    {
        switch (eventName)
        {
            case "login":
                ResolveAcknowledgement(_loginAcknowledgement, root, "OKX login");
                break;
            case "subscribe":
                ResolveAcknowledgement(_subscriptionAcknowledgement, root, "OKX subscribe");
                break;
            case "error":
                var message = root.TryGetProperty("msg", out var errorMessage) ? errorMessage.GetString() : "Unknown OKX WebSocket error.";
                var exception = new InvalidOperationException(message);
                _loginAcknowledgement?.TrySetException(exception);
                _subscriptionAcknowledgement?.TrySetException(exception);
                throw exception;
        }
    }

    private static void ResolveAcknowledgement(TaskCompletionSource<bool>? acknowledgement, JsonElement root, string operation)
    {
        if (acknowledgement is null)
        {
            return;
        }

        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? "0" : "0";
        if (code == "0")
        {
            acknowledgement.TrySetResult(true);
            return;
        }

        var message = root.TryGetProperty("msg", out var messageElement) ? messageElement.GetString() : "Unknown error.";
        acknowledgement.TrySetException(new InvalidOperationException($"{operation} failed: {message}"));
    }

    private static TaskCompletionSource<bool> NewAcknowledgement()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AwaitAcknowledgementAsync(
        TaskCompletionSource<bool> acknowledgement,
        string operation,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await acknowledgement.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for OKX {operation} acknowledgement.");
        }
    }

    private string ResolveWebSocketUrl()
        => _configuration.GetValue<bool>("OKX:IsDemoMode")
            ? DemoPrivateWebSocketUrl
            : ProductionPrivateWebSocketUrl;

    private void EnsureCredentialsPresent()
    {
        if (string.IsNullOrWhiteSpace(_configuration["OKX:ApiKey"]) ||
            string.IsNullOrWhiteSpace(_configuration["OKX:Passphrase"]) ||
            (_apiVault is null && string.IsNullOrWhiteSpace(_configuration["OKX:SecretKey"])))
        {
            throw new InvalidOperationException("OKX execution sync requires ApiKey, Passphrase, and SecretKey credentials.");
        }
    }

    internal string CreateLoginMessage()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeLoginSignature(timestamp);

        return JsonSerializer.Serialize(new
        {
            op = "login",
            args = new[]
            {
                new
                {
                    apiKey = _configuration["OKX:ApiKey"],
                    passphrase = _configuration["OKX:Passphrase"],
                    timestamp,
                    sign = signature
                }
            }
        });
    }

    internal static string CreateSubscribeMessage()
        => JsonSerializer.Serialize(new
        {
            op = "subscribe",
            args = new[]
            {
                new
                {
                    channel = "orders",
                    instType = "SPOT"
                }
            }
        });

    internal string ComputeLoginSignature(string timestamp)
    {
        var payload = $"{timestamp}GET/users/self/verify";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        if (_apiVault is not null)
        {
            var secretBytes = _apiVault.GetTransientDecryptedKey();
            try
            {
                using var hmac = new HMACSHA256(secretBytes);
                return Convert.ToBase64String(hmac.ComputeHash(payloadBytes));
            }
            finally
            {
                Array.Clear(secretBytes, 0, secretBytes.Length);
            }
        }

        using var fallbackHmac = new HMACSHA256(Encoding.UTF8.GetBytes(_configuration["OKX:SecretKey"] ?? string.Empty));
        return Convert.ToBase64String(fallbackHmac.ComputeHash(payloadBytes));
    }

    internal static bool TryParseExecutionReports(JsonElement root, out IReadOnlyList<ExecutionReport> reports)
    {
        reports = Array.Empty<ExecutionReport>();

        if (!root.TryGetProperty("arg", out var argElement) ||
            !argElement.TryGetProperty("channel", out var channelElement) ||
            !string.Equals(channelElement.GetString(), "orders", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("data", out var dataElement) ||
            dataElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsedReports = new List<ExecutionReport>();
        foreach (var order in dataElement.EnumerateArray())
        {
            parsedReports.Add(ParseExecutionReport(order));
        }

        reports = parsedReports;
        return parsedReports.Count > 0;
    }

    internal static ExecutionReport ParseExecutionReport(JsonElement order)
    {
        var symbol = OkxExecutionService.NormalizeFromOkx(GetString(order, "instId"));
        var clientOrderId = GetString(order, "clOrdId");
        var exchangeOrderId = GetString(order, "ordId");
        var requestedQuantity = GetDecimal(order, "sz");
        var filledQuantity = GetDecimal(order, "accFillSz");
        var remainingQuantity = Math.Max(0m, requestedQuantity - filledQuantity);
        var averagePrice = GetDecimal(order, "avgPx");
        var fallbackPrice = GetDecimal(order, "px");
        if (averagePrice <= 0m)
        {
            averagePrice = fallbackPrice;
        }

        var lastFillPrice = GetDecimal(order, "fillPx", averagePrice);
        var lastFillQuantity = GetDecimal(order, "fillSz");
        var timestamp = ResolveTimestamp(order);
        var venueStatus = GetString(order, "state");

        return new ExecutionReport(
            symbol,
            clientOrderId,
            exchangeOrderId,
            filledQuantity,
            remainingQuantity,
            averagePrice,
            lastFillPrice,
            lastFillQuantity,
            NormalizeStatus(venueStatus, filledQuantity, remainingQuantity),
            0,
            timestamp);
    }

    private static string NormalizeStatus(string venueStatus, decimal filledQuantity, decimal remainingQuantity)
    {
        return venueStatus.Trim().ToLowerInvariant() switch
        {
            "live" => filledQuantity > 0m ? "PARTIALLY_FILLED" : "NEW",
            "partially_filled" => "PARTIALLY_FILLED",
            "filled" => "FILLED",
            "canceled" => filledQuantity > 0m ? "PARTIALLY_FILLED" : "CANCELED",
            "mmp_canceled" => "CANCELED",
            "order_failed" => "REJECTED",
            _ => filledQuantity > 0m && remainingQuantity <= 0m
                ? "FILLED"
                : filledQuantity > 0m
                    ? "PARTIALLY_FILLED"
                    : "NEW"
        };
    }

    private static DateTime ResolveTimestamp(JsonElement order)
    {
        foreach (var propertyName in new[] { "uTime", "fillTime", "cTime" })
        {
            if (!order.TryGetProperty(propertyName, out var timestampElement))
            {
                continue;
            }

            if (long.TryParse(timestampElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var epochMs) &&
                epochMs > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            }
        }

        return DateTime.UtcNow;
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty;

    private static decimal GetDecimal(JsonElement element, string propertyName, decimal fallback = 0m)
        => element.TryGetProperty(propertyName, out var property) &&
           decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static Task SendTextAsync(ClientWebSocket socket, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task CloseSocketAsync()
    {
        if (_webSocket is null)
        {
            return;
        }

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while closing OKX execution sync socket.");
        }
        finally
        {
            _webSocket.Dispose();
            _webSocket = null;
        }
    }
}
