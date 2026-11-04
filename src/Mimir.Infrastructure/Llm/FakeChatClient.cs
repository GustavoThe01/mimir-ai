using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Mimir.Infrastructure.Llm;

/// <summary>Cliente sem rede e sem custo: repete a última pergunta. Deixa os testes e o CI determinísticos.</summary>
public sealed class FakeChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, Answer(messages));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
    }

    private static string Answer(IEnumerable<ChatMessage> messages) =>
        $"[fake] {messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text}";
}
