using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Middleware;

/// <summary>
/// Validates incoming webhook requests using HMAC-SHA256 signature.
/// Expects the signature in the "X-Signature-256" header.
/// 
/// To generate a valid signature for testing:
///   - Body = JSON payload as UTF-8
///   - Signature = HMAC-SHA256(Secret, Body) → hex string prefixed with "sha256="
/// </summary>
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
        // Only validate POST requests to webhook endpoints
        if (context.Request.Method == HttpMethods.Post && 
            context.Request.Path.StartsWithSegments("/webhook"))
        {
            if (string.IsNullOrWhiteSpace(_secret))
            {
                _logger.LogError("WebhookSecret is not configured. Rejecting webhook request.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("Webhook secret is not configured.");
                return;
            }

            // Enable buffering so we can read the body multiple times
            context.Request.EnableBuffering();

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0; // Reset for the next middleware/handler

            if (!context.Request.Headers.TryGetValue(SignatureHeader, out var signatureHeader))
            {
                _logger.LogWarning("Webhook request rejected: Missing {Header} from {IP}",
                    SignatureHeader,
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing signature header.");
                return;
            }

            if (!context.Request.Headers.TryGetValue(TimestampHeader, out var timestampHeader) ||
                !long.TryParse(timestampHeader.ToString(), out var unixTimestamp))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing or invalid timestamp.");
                return;
            }

            var requestTimestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
            var now = DateTimeOffset.UtcNow;
            if ((now - requestTimestamp).Duration() > _maxSkew)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Request timestamp is outside the allowed window.");
                return;
            }

            if (!context.Request.Headers.TryGetValue(NonceHeader, out var nonceHeader) ||
                string.IsNullOrWhiteSpace(nonceHeader.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing nonce header.");
                return;
            }

            if (!context.Request.Headers.TryGetValue(IdempotencyHeader, out var idempotencyHeader) ||
                string.IsNullOrWhiteSpace(idempotencyHeader.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync("Missing idempotency key.");
                return;
            }

            var expectedSignature = ComputeSignature(body);
            var receivedSignature = signatureHeader.ToString();

            if (!CryptographicEquals(expectedSignature, receivedSignature))
            {
                _logger.LogWarning("Webhook request rejected: Invalid signature from {IP}",
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid signature.");
                return;
            }

            var nonceCacheKey = $"webhook-nonce:{nonceHeader}";
            if (_cache.TryGetValue(nonceCacheKey, out _))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync("Duplicate nonce detected.");
                return;
            }

            var idempotencyCacheKey = $"webhook-idempotency:{idempotencyHeader}";
            if (_cache.TryGetValue(idempotencyCacheKey, out _))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync("Duplicate idempotency key detected.");
                return;
            }

            var idempotencyKey = idempotencyHeader.ToString();
            var nonce = nonceHeader.ToString();
            var existsInDb = await dbContext.WebhookRequestLogs.AnyAsync(x =>
                x.IdempotencyKey == idempotencyKey || x.Nonce == nonce);
            if (existsInDb)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync("Duplicate webhook request detected.");
                return;
            }

            var entryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _maxSkew
            };
            _cache.Set(nonceCacheKey, true, entryOptions);
            _cache.Set(idempotencyCacheKey, true, entryOptions);
            dbContext.WebhookRequestLogs.Add(new WebhookRequestLog
            {
                IdempotencyKey = idempotencyKey,
                Nonce = nonce,
                Signature = receivedSignature,
                ReceivedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.Add(_maxSkew)
            });
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Webhook signature validated successfully.");
        }

        await _next(context);
    }

    private string ComputeSignature(string payload)
    {
        if (string.IsNullOrWhiteSpace(_secret))
        {
            throw new InvalidOperationException("Webhook secret is not configured.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Constant-time comparison to prevent timing attacks.
    /// </summary>
    private static bool CryptographicEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }
}
