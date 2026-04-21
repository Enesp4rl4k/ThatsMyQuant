using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;

namespace ThatsMyQuant.Infrastructure.Ai;

public class MockChatCompletionService : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, 
        PromptExecutionSettings? executionSettings = null, 
        Kernel? kernel = null, 
        CancellationToken cancellationToken = default)
    {
        var result = new List<ChatMessageContent>
        {
            new ChatMessageContent(AuthorRole.Assistant, "AI Reasoning is in SIMULATION mode (No API Key). DECISION: APPROVE. REASONING: Simulation bypass.")
        };
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>(result);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new StreamingChatMessageContent(
            AuthorRole.Assistant,
            "AI Reasoning is in SIMULATION mode (No API Key). DECISION: APPROVE. REASONING: Simulation bypass.");
        await Task.CompletedTask;
    }
}
