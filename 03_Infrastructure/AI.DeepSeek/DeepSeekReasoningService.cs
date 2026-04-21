using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Ai;

public class DeepSeekReasoningService : IReasoningProvider
{
    private readonly ILogger<DeepSeekReasoningService> _logger;
    private readonly IChatCompletionService _chatService;
    private string _lastReasoning = string.Empty;

    public DeepSeekReasoningService(ILogger<DeepSeekReasoningService> logger, IChatCompletionService chatService)
    {
        _logger = logger;
        _chatService = chatService;
    }

    public async Task<bool> ShouldExecuteAsync(Signal signal)
    {
        _logger.LogInformation("AI Reasoning started for {Symbol}...", signal.Symbol);

        var prompt = $@"
        Sen bir kantitatif ticaret botu 'The Brain' (DeepSeek-R1 tabanlı) bileşenisin.
        Aşağıdaki sinyali ve piyasa bağlamını analiz et.

        SİNYAL:
        - Sembol: {signal.Symbol}
        - Yön: {signal.Side}
        - Fiyat: {signal.Price}
        - Kaynak: {signal.Source}

        PİYASA YAPISI (STRÜKTÜR):
        - Durum: {signal.Metadata.GetValueOrDefault("MarketStructure", "Unknown")}
        - Analiz: {signal.Metadata.GetValueOrDefault("StructureDescription", "No structural data available.")}

        ANALİZ KURALLARI:
        1. Sinyal teknik olarak mantıklı mı?
        2. Piyasa yapısı (Range, Trap vb.) bu işlemi destekliyor mu? (Örn: Range tepesinde Buy risklidir)
        3. Duygu analizi pozitif mi? (Haber varsa)
        4. Risk/Ödül oranı uygun mu?

        KARAR:
        Lütfen kararını şu formatta ver:
        DECISION: [APPROVE/REJECT]
        REASONING: [Nedenini tek cümlede açıkla]
        ";

        try
        {
            var results = await _chatService.GetChatMessageContentsAsync(prompt);
            _lastReasoning = results.FirstOrDefault()?.Content ?? "No reasoning provided.";

            if (_lastReasoning.Contains("SIMULATION", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("DeepSeek is in simulation mode — approving by default.");
                _lastReasoning = "Simulation mode — AI inference disabled. Defaulting to APPROVE.";
                return true;
            }

            _logger.LogInformation("AI Decision: {Reasoning}", _lastReasoning);

            return _lastReasoning.Contains("APPROVE", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Reasoning failed. Falling back to Safety REJECT.");
            _lastReasoning = "Error in AI processing: " + ex.Message;
            return false;
        }
    }

    public Task<string> GetReasoningAsync(Signal signal)
    {
        return Task.FromResult(_lastReasoning);
    }
}
