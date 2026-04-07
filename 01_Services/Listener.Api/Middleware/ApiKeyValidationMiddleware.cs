namespace Listener.Api.Middleware;

public sealed class ApiKeyValidationMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyValidationMiddleware> _logger;
    private readonly string? _apiKey;

    public ApiKeyValidationMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<ApiKeyValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _apiKey = configuration["Security:InternalApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isExemptPath = path.StartsWithSegments("/webhook") ||
                           path.StartsWithSegments("/health") ||
                           path.StartsWithSegments("/ready") ||
                           path.StartsWithSegments("/metrics") ||
                           path.StartsWithSegments("/metrics.json") ||
                           path.StartsWithSegments("/ops");
        if (isExemptPath)
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogError("Security:InternalApiKey is not configured.");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Internal API key is not configured.");
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var suppliedKey) ||
            !string.Equals(suppliedKey.ToString(), _apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid API key.");
            return;
        }

        await _next(context);
    }
}
