using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Infrastructure.Ai;

namespace Listener.Api.Services;

/// <summary>
/// Provider routing for the Backtest Lab interpreter (and any future SK-backed
/// AI feature). Picks a chat-completion implementation at startup based on the
/// <c>TMQ_INTERPRETER_PROVIDER</c> env var (or <c>Interpreter:Provider</c>
/// config key). Falls back to <see cref="MockChatCompletionService"/> when no
/// provider is configured so the worker still boots.
/// </summary>
/// <remarks>
/// <para><b>Why one connector for four providers:</b> OpenAI, DeepSeek, Ollama,
/// and Anthropic-via-OpenRouter all expose the OpenAI Chat Completions wire
/// format. The SK <c>OpenAIChatCompletionService</c> takes a custom base URL +
/// model id, so we can route to all of them by changing two strings — no extra
/// NuGet package per vendor (adds 10+ MB and CVE surface area).</para>
///
/// <para>If the operator wants Anthropic's NATIVE format (with prompt caching
/// + tool_use), they'd switch to OpenRouter's anthropic/* models — same wire
/// shape, server-side does the translation. Native Anthropic SDK in SK
/// requires a separate connector package (Microsoft.SemanticKernel.Connectors.
/// Anthropic, preview); skipped here to keep deps lean.</para>
///
/// <para><b>Env vars:</b>
/// <code>
/// TMQ_INTERPRETER_PROVIDER=openai     (default if OPENAI_API_KEY set)
///                        =deepseek    (cheap, OpenAI-compatible)
///                        =openrouter  (Claude / Llama / etc. via OpenRouter)
///                        =ollama      (local, free, no key required)
///                        =gemini      (legacy path; uses Google connector)
///                        =mock        (tests / no-network smoke runs)
///
/// TMQ_INTERPRETER_MODEL   override the default model id
/// TMQ_INTERPRETER_API_KEY override key (else picks per-provider env var)
/// TMQ_INTERPRETER_BASE_URL override endpoint (rare)
/// </code></para>
/// </remarks>
internal static class InterpreterProviderRouter
{
    public sealed record RouterConfig(
        string Provider,
        string Model,
        string ApiKey,
        Uri    BaseUrl);

    public static void AddInterpreterChatCompletion(this IServiceCollection services,
                                                    IConfiguration config)
    {
        var cfg = ResolveConfig(config);

        switch (cfg.Provider)
        {
            case "mock":
                services.AddKernel();
                services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
                return;

            case "gemini":
                // Legacy path — Gemini doesn't speak OpenAI-compat, so it gets the dedicated connector.
                if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                {
                    services.AddKernel();
                    services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
                    return;
                }
                services.AddKernel().AddGoogleAIGeminiChatCompletion(cfg.Model, cfg.ApiKey);
                return;

            case "ollama":
                // Ollama exposes OpenAI-compat at /v1. No API key required, but the
                // connector requires *something* — pass a placeholder so it doesn't
                // bail at construction.
                services.AddKernel().AddOpenAIChatCompletion(
                    modelId: cfg.Model,
                    endpoint: cfg.BaseUrl,
                    apiKey: cfg.ApiKey.Length > 0 ? cfg.ApiKey : "ollama-no-key-required");
                return;

            case "deepseek":
            case "openrouter":
            case "openai":
            default:
                if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                {
                    services.AddKernel();
                    services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
                    return;
                }
                services.AddKernel().AddOpenAIChatCompletion(
                    modelId: cfg.Model,
                    endpoint: cfg.BaseUrl,
                    apiKey: cfg.ApiKey);
                return;
        }
    }

    /// <summary>Resolve provider + model + key + URL from env/config.
    /// Defaults aim to "just work" if a single recognised key is present.</summary>
    private static RouterConfig ResolveConfig(IConfiguration config)
    {
        // Env vars trump config file so deploys can swap provider without rebuild.
        var explicitProvider = (Environment.GetEnvironmentVariable("TMQ_INTERPRETER_PROVIDER")
                                ?? config["Interpreter:Provider"] ?? "").Trim().ToLowerInvariant();

        var openaiKey   = Environment.GetEnvironmentVariable("OPENAI_API_KEY")   ?? "";
        var deepseekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        var openrouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
        var geminiKey   = config["Ai:GeminiApiKey"] ?? "";

        // Auto-pick based on which key is present, preferring the cheapest path
        // when multiple keys are set. Operator can override with TMQ_INTERPRETER_PROVIDER.
        var provider = explicitProvider;
        if (string.IsNullOrEmpty(provider))
        {
            provider = !string.IsNullOrEmpty(deepseekKey) ? "deepseek"
                     : !string.IsNullOrEmpty(openrouterKey) ? "openrouter"
                     : !string.IsNullOrEmpty(openaiKey)   ? "openai"
                     : !string.IsNullOrEmpty(geminiKey)   ? "gemini"
                                                          : "mock";
        }

        var explicitKey = Environment.GetEnvironmentVariable("TMQ_INTERPRETER_API_KEY") ?? "";
        var explicitModel = Environment.GetEnvironmentVariable("TMQ_INTERPRETER_MODEL") ?? "";
        var explicitUrl = Environment.GetEnvironmentVariable("TMQ_INTERPRETER_BASE_URL") ?? "";

        return provider switch
        {
            "deepseek" => new RouterConfig(
                Provider: "deepseek",
                Model:    explicitModel.Length > 0 ? explicitModel : "deepseek-chat",
                ApiKey:   explicitKey.Length > 0   ? explicitKey   : deepseekKey,
                BaseUrl:  new Uri(explicitUrl.Length > 0 ? explicitUrl : "https://api.deepseek.com/v1")),

            "openrouter" => new RouterConfig(
                Provider: "openrouter",
                Model:    explicitModel.Length > 0 ? explicitModel : "anthropic/claude-3.5-sonnet",
                ApiKey:   explicitKey.Length > 0   ? explicitKey   : openrouterKey,
                BaseUrl:  new Uri(explicitUrl.Length > 0 ? explicitUrl : "https://openrouter.ai/api/v1")),

            "ollama" => new RouterConfig(
                Provider: "ollama",
                Model:    explicitModel.Length > 0 ? explicitModel : "llama3.1:8b",
                ApiKey:   explicitKey, // optional; Ollama doesn't enforce
                BaseUrl:  new Uri(explicitUrl.Length > 0 ? explicitUrl : "http://localhost:11434/v1")),

            "gemini" => new RouterConfig(
                Provider: "gemini",
                Model:    explicitModel.Length > 0 ? explicitModel : "gemini-pro",
                ApiKey:   explicitKey.Length > 0 ? explicitKey : geminiKey,
                // Gemini doesn't use BaseUrl in its connector; placeholder.
                BaseUrl:  new Uri("https://generativelanguage.googleapis.com")),

            "mock" => new RouterConfig(
                Provider: "mock",
                Model:    "mock",
                ApiKey:   "",
                BaseUrl:  new Uri("https://localhost")),

            _ /* "openai" + fallback */ => new RouterConfig(
                Provider: "openai",
                Model:    explicitModel.Length > 0 ? explicitModel : "gpt-4o-mini",
                ApiKey:   explicitKey.Length > 0   ? explicitKey   : openaiKey,
                BaseUrl:  new Uri(explicitUrl.Length > 0 ? explicitUrl : "https://api.openai.com/v1")),
        };
    }
}
