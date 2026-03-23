using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Middleware;

public class HmacValidationMiddleware
{
    private const string SignatureHeader = "X-Signature-256";
    private const string TimestampHeader = "X-Timestamp";
    private const string NonceHeader = "X-Nonce";
    private const string IdempotencyHeader = "X-Idempotency-Key";

    private readonly RequestDelegate _next;
    private readonly string? _secret;
    private readonly TimeSpan _maxSkew;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HmacValidationMiddleware> _logger;

    public HmacValidationMiddleware(
        RequestDelegate next,
        IConfiguration config,
        IMemoryCache cache,
        ILogger<HmacValidationMiddleware> logger)
    {
        _next = next;
        _secret = config["WebhookSecret"];
        _maxSkew = TimeSpan.FromMinutes(config.GetValue("Security:WebhookTimestampWindowMinutes", 5));
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, QuantDbContext dbContext)
    {
        if (!IsWebhookRequest(context))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_secret))
        {
            _logger.LogCritical("WebhookSecret missing! Rejecting all incoming signals.");
            await SendResponse(context, StatusCodes.Status500InternalServerError, "Security configuration error.");
            return;
        }

        // 1. Pre-validation (Headers and Timing)
        if (!TryValidateHeaders(context, out var headers))
        {
            await SendResponse(context, StatusCodes.Status401Unauthorized, "Missing security headers.");
            return;
        }

        if (!IsTimestampValid(headers.Timestamp))
        {
            await SendResponse(context, StatusCodes.Status401Unauthorized, "Timestamp outside allowed window.");
            return;
        }

        // 2. Cryptographic Validation
        context.Request.EnableBuffering();
        var body = await ReadBodyAsync(context.Request);

        if (!IsSignatureValid(body, headers.Signature))
        {
            _logger.LogWarning("Invalid HMAC signature from {IP}", context.Connection.RemoteIpAddress);
            await SendResponse(context, StatusCodes.Status401Unauthorized, "Invalid signature.");
            return;
        }

        // 3. Replay Attack & Idempotency Protection
        if (await IsDuplicateRequest(headers.Nonce, headers.Idempotency, dbContext))
        {
            await SendResponse(context, StatusCodes.Status409Conflict, "Duplicate request detected.");
            return;
        }

        // 4. Finalize and Log
        await RegisterSuccessfulRequest(headers, dbContext);
        await _next(context);
    }

    private static bool IsWebhookRequest(HttpContext context) =>
        context.Request.Method == HttpMethods.Post && context.Request.Path.StartsWithSegments("/webhook");

    private bool TryValidateHeaders(HttpContext context, out (string Signature, string Timestamp, string Nonce, string Idempotency) headers)
    {
        headers = (
            context.Request.Headers[SignatureHeader].ToString(),
            context.Request.Headers[TimestampHeader].ToString(),
            context.Request.Headers[NonceHeader].ToString(),
            context.Request.Headers[IdempotencyHeader].ToString()
        );

        return !string.IsNullOrWhiteSpace(headers.Signature) &&
               !string.IsNullOrWhiteSpace(headers.Timestamp) &&
               !string.IsNullOrWhiteSpace(headers.Nonce) &&
               !string.IsNullOrWhiteSpace(headers.Idempotency);
    }

    private bool IsTimestampValid(string timestampStr)
    {
        if (!long.TryParse(timestampStr, out var unixTs)) return false;
        var requestTime = DateTimeOffset.FromUnixTimeSeconds(unixTs);
        return (DateTimeOffset.UtcNow - requestTime).Duration() <= _maxSkew;
    }

    private bool IsSignatureValid(string body, string receivedSignature)
    {
        var expected = ComputeSignature(body);
        return CryptographicEquals(expected, receivedSignature);
    }

    private async Task<bool> IsDuplicateRequest(string nonce, string idempotency, QuantDbContext db)
    {
        if (_cache.TryGetValue($"webhook:{nonce}", out _) || _cache.TryGetValue($"webhook:{idempotency}", out _))
            return true;

        return await db.WebhookRequestLogs.AnyAsync(x => x.IdempotencyKey == idempotency || x.Nonce == nonce);
    }

    private async Task RegisterSuccessfulRequest((string Signature, string Timestamp, string Nonce, string Idempotency) h, QuantDbContext db)
    {
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _maxSkew };
        _cache.Set($"webhook:{h.Nonce}", true, options);
        _cache.Set($"webhook:{h.Idempotency}", true, options);

        db.WebhookRequestLogs.Add(new WebhookRequestLog
        {
            IdempotencyKey = h.Idempotency,
            Nonce = h.Nonce,
            Signature = h.Signature,
            ReceivedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.Add(_maxSkew)
        });
        await db.SaveChangesAsync();
    }

    private string ComputeSignature(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret!));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    private static bool CryptographicEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }

    private static async Task SendResponse(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsync(message);
    }
}