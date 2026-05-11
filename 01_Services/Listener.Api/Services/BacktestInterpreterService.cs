using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Listener.Api.Services;

/// <summary>
/// LLM-powered interpretation of Backtest Lab analysis results. Takes the
/// numeric output of the analyze endpoint (regression stats + entropy summary +
/// MC fan headline figures), feeds them to whatever <see cref="IChatCompletionService"/>
/// is registered (today: DeepSeek via SK; could be Claude / OpenAI / Ollama
/// without code changes), and returns a plain-Turkish operator narrative.
/// </summary>
/// <remarks>
/// <para><b>Why a separate service vs. reusing IReasoningProvider:</b>
/// IReasoningProvider's contract is bound to the trading <c>Signal</c> model
/// (ShouldExecuteAsync, GetReasoningAsync). Backtest interpretation is a
/// different domain — different inputs, different output shape, different
/// failure semantics (LLM error must NOT block the dashboard render).
/// Cleaner to add a focused service than overload the existing contract.</para>
///
/// <para><b>Provider-agnostic by construction:</b> we depend only on
/// <see cref="IChatCompletionService"/>. Today the registered impl is the
/// SK-OpenAI connector pointed at DeepSeek; swapping to Claude (via
/// SK Anthropic connector) or local Ollama is a 1-line DI change in
/// Listener.Api/Program.cs. The Sprint 3 G6 multi-provider dropdown will
/// register multiple keyed services and route at the endpoint level.</para>
///
/// <para><b>Pre-reg discipline:</b> The interpreter NEVER recommends
/// "deploy this strategy" / "add to portfolio". It explains numbers in plain
/// language. The system prompt explicitly forbids action advice. Promotion
/// remains a human-only decision per the manual review template.</para>
/// </remarks>
public sealed class BacktestInterpreterService
{
    private readonly IChatCompletionService _chat;
    private readonly ILogger<BacktestInterpreterService> _logger;

    public BacktestInterpreterService(
        IChatCompletionService chat,
        ILogger<BacktestInterpreterService> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    /// <summary>Generate a plain-Turkish operator narrative for an analysis bundle.</summary>
    /// <returns>Markdown text. Empty string + logged warning on LLM failure
    /// (caller should display "interpretation unavailable" rather than fail).</returns>
    public async Task<BacktestInterpretation> InterpretAsync(
        BacktestInterpretationRequest req,
        CancellationToken ct = default)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));

        var userPrompt = BuildUserPrompt(req);
        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(userPrompt);

        try
        {
            var response = await _chat.GetChatMessageContentAsync(history, cancellationToken: ct)
                .ConfigureAwait(false);
            var text = response?.Content ?? string.Empty;
            _logger.LogInformation(
                "BacktestInterpreter: response received ({Chars} chars)", text.Length);
            return new BacktestInterpretation(text, ProviderHint(_chat));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BacktestInterpreter LLM call failed");
            return new BacktestInterpretation(
                Markdown: string.Empty,
                Provider: ProviderHint(_chat),
                Error: $"interpreter_failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Prompt construction
    // ---------------------------------------------------------------------

    private const string SystemPrompt =
        "Sen bir kantitatif analiz asistanısın. Operatör sana bir backtest'in 3 görünümünden " +
        "(Monte Carlo equity fan, lineer regresyon decomposition, rolling Shannon entropy) " +
        "elde edilmiş sayısal sonuçları verir. Görevin bu sayıları **Türkçe**, **kısa** " +
        "(maks 250 kelime), **operatör için faydalı** bir dilde yorumlamak.\n\n" +
        "FORMAT:\n" +
        "## Özet (1-2 cümle)\n" +
        "## Gözlemler\n" +
        "- bullet 1\n" +
        "- bullet 2\n" +
        "- bullet 3 (max 5)\n" +
        "## Risk Bayrakları\n" +
        "- ⚠ ile başlayan flag varsa\n" +
        "## Önerilen Sonraki Adım\n" +
        "Sadece **gözlemsel** öneriler (örn. 'OOS holdout test çalıştır', 'farklı sembolde " +
        "tekrar et'). ASLA 'bu stratejiyi canlıya al' / 'pozisyon büyüt' / 'sermaye artır' " +
        "tavsiyesi VERME — promotion kararı her zaman insan operatöre aittir, manuel review " +
        "checklist'inden geçer.\n\n" +
        "Sayıları analiz et, çıkarımları belirt, ama harekete geçirici talimat verme.";

    private static string BuildUserPrompt(BacktestInterpretationRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BACKTEST ANALYSIS BUNDLE:");
        sb.AppendLine();
        sb.AppendLine($"Symbol/strategy: {r.Label ?? "(unspecified)"}");
        sb.AppendLine($"Bars analyzed: {r.NObservations}");
        sb.AppendLine($"Start equity: ${r.StartEquity:N2}");
        sb.AppendLine($"End equity: ${r.EndEquity:N2}");
        sb.AppendLine(
            $"Total return: {((r.EndEquity / Math.Max(r.StartEquity, 1m) - 1m) * 100m):+0.00;-0.00;0.00}%");
        sb.AppendLine();
        sb.AppendLine("--- LINEAR REGRESSION ---");
        sb.AppendLine($"Slope (USD/bar): {r.RegressionSlopeUsdPerBar:F4}");
        sb.AppendLine($"R²: {r.RegressionRSquared:F4}");
        sb.AppendLine($"Residual σ (USD): {r.RegressionResidualStdUsd:F2}");
        sb.AppendLine();
        sb.AppendLine("--- MONTE CARLO FAN (bootstrap of bar returns) ---");
        sb.AppendLine($"Iterations: {r.McIterations}");
        sb.AppendLine($"End: realised=${r.EndEquity:N2} vs median=${r.McMedianEnd:N2} " +
                      $"(p05=${r.McP05End:N2}, p95=${r.McP95End:N2})");
        sb.AppendLine();
        sb.AppendLine("--- SHANNON ENTROPY (rolling) ---");
        sb.AppendLine($"Window: {r.EntropyWindow} bars, bins: {r.EntropyBins}");
        sb.AppendLine($"Max entropy (nats): {r.EntropyMaxNats:F3}");
        sb.AppendLine($"Last entropy: {r.EntropyLastNats:F3} " +
                      $"({(r.EntropyMaxNats > 0 ? (r.EntropyLastNats / r.EntropyMaxNats) * 100 : 0):F1}% of max)");
        return sb.ToString();
    }

    private static string ProviderHint(IChatCompletionService svc) =>
        svc.GetType().Name; // good enough for the dashboard "powered by" line
}

// ---------------------------------------------------------------------------
// Request / response DTOs
// ---------------------------------------------------------------------------

public sealed record BacktestInterpretationRequest(
    string?  Label,
    int      NObservations,
    decimal  StartEquity,
    decimal  EndEquity,
    double   RegressionSlopeUsdPerBar,
    double   RegressionRSquared,
    double   RegressionResidualStdUsd,
    int      McIterations,
    decimal  McMedianEnd,
    decimal  McP05End,
    decimal  McP95End,
    int      EntropyWindow,
    int      EntropyBins,
    double   EntropyMaxNats,
    double   EntropyLastNats);

public sealed record BacktestInterpretation(
    string  Markdown,
    string  Provider,
    string? Error = null);
