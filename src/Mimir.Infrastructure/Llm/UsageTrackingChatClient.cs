using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Mimir.Infrastructure.Llm;

/// <summary>Uma chamada ao modelo: quantos tokens entraram, quantos saíram e quanto tempo levou.</summary>
/// <remarks>Os tokens ficam nulos quando o provedor não informa o uso.</remarks>
public sealed record LlmCall(long? InputTokens, long? OutputTokens, TimeSpan Elapsed);

/// <summary>Acumula as chamadas de uma execução. Numa API paga, é daqui que sai a estimativa de custo.</summary>
public sealed class UsageTracker
{
    private readonly List<LlmCall> _calls = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LlmCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public LlmCall? Last
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count == 0 ? null : _calls[^1];
            }
        }
    }

    public long InputTokens => Calls.Sum(c => c.InputTokens ?? 0);

    public long OutputTokens => Calls.Sum(c => c.OutputTokens ?? 0);

    internal void Record(LlmCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }
}

/// <summary>
/// Middleware que registra tokens e tempo de cada chamada, no log e no <see cref="UsageTracker"/>.
/// </summary>
/// <remarks>
/// Fica por fora da resiliência: o tempo medido é o que o usuário esperou, incluindo as novas tentativas.
/// </remarks>
public sealed partial class UsageTrackingChatClient(
    IChatClient innerClient,
    UsageTracker tracker,
    TimeProvider timeProvider,
    ILogger logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var start = timeProvider.GetTimestamp();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Record(response.Usage, timeProvider.GetElapsedTime(start));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = timeProvider.GetTimestamp();
        UsageDetails? usage = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // No streaming, o uso chega como conteúdo de uma das atualizações, normalmente a última.
            foreach (var content in update.Contents.OfType<UsageContent>())
            {
                usage = content.Details;
            }

            yield return update;
        }

        Record(usage, timeProvider.GetElapsedTime(start));
    }

    private void Record(UsageDetails? usage, TimeSpan elapsed)
    {
        var call = new LlmCall(usage?.InputTokenCount, usage?.OutputTokenCount, elapsed);
        tracker.Record(call);
        LogCall(logger, call.InputTokens, call.OutputTokens, elapsed.TotalMilliseconds);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chamada ao modelo: {InputTokens} tokens de entrada, {OutputTokens} de saída, {ElapsedMs:0} ms")]
    private static partial void LogCall(ILogger logger, long? inputTokens, long? outputTokens, double elapsedMs);
}
