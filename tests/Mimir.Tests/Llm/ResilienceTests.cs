using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Infrastructure.Llm;

namespace Mimir.Tests.Llm;

public class ResilienceTests
{
    private static readonly ResilienceSettings Settings =
        new(TimeSpan.FromSeconds(5), MaxAttempts: 3, BaseDelay: TimeSpan.Zero, "O Ollama de teste", "Confira se ele está rodando.");

    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    /// <summary>Executa um roteiro por chamada: lança a exceção, espera para sempre ou responde.</summary>
    private sealed class ScriptedClient(params Func<CancellationToken, Task<string>>[] script) : IChatClient
    {
        public int Calls { get; private set; }

        public UsageDetails? Usage { get; init; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = await script[Calls++](cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = Usage };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = await script[Calls++](cancellationToken);
            foreach (var word in text.Split(' '))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            }

            if (Usage is not null)
            {
                yield return new ChatResponseUpdate { Contents = [new UsageContent(Usage)] };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static Func<CancellationToken, Task<string>> Fail(Exception exception) => _ => Task.FromException<string>(exception);

    private static Func<CancellationToken, Task<string>> Answer(string text) => _ => Task.FromResult(text);

    private static Func<CancellationToken, Task<string>> Hang() => async token =>
    {
        await Task.Delay(Timeout.Infinite, token);
        return "nunca";
    };

    private static HttpRequestException Refused() => new("Conexão recusada");

    private static ResilientChatClient Resilient(IChatClient inner, ResilienceSettings? settings = null) =>
        new(inner, settings ?? Settings, TimeProvider.System, NullLogger.Instance);

    [Fact]
    public async Task TransientFailures_AreRetried_UntilTheAnswerArrives()
    {
        using var inner = new ScriptedClient(Fail(Refused()), Fail(new HttpRequestException("ocupado", null, HttpStatusCode.ServiceUnavailable)), Answer("ok"));
        using var client = Resilient(inner);

        var response = await client.GetResponseAsync("oi", cancellationToken: _token);

        Assert.Equal("ok", response.Text);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task AfterTheLastAttempt_TheErrorIsFriendly()
    {
        using var inner = new ScriptedClient(Fail(Refused()), Fail(Refused()), Fail(Refused()));
        using var client = Resilient(inner);

        var exception = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.GetResponseAsync("oi", cancellationToken: _token));

        Assert.Equal(
            "O Ollama de teste não respondeu depois de 3 tentativa(s): não foi possível conectar. Confira se ele está rodando.",
            exception.Message);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        using var inner = new ScriptedClient(Fail(new HttpRequestException("chave inválida", null, HttpStatusCode.Unauthorized)), Answer("ok"));
        using var client = Resilient(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync("oi", cancellationToken: _token));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task SlowAttempt_TimesOut_AndIsRetried()
    {
        using var inner = new ScriptedClient(Hang(), Answer("ok"));
        using var client = Resilient(inner, Settings with { Timeout = TimeSpan.FromMilliseconds(50) });

        var response = await client.GetResponseAsync("oi", cancellationToken: _token);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CancellationByTheCaller_IsNotRetried()
    {
        using var inner = new ScriptedClient(Hang(), Answer("ok"));
        using var client = Resilient(inner);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("oi", cancellationToken: cancel.Token));

        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void HttpStatus_DecidesWhatIsTransient(HttpStatusCode status, bool transient)
    {
        Assert.Equal(transient, ResilientChatClient.IsTransient(new HttpRequestException("x", null, status), CancellationToken.None));
    }

    [Fact]
    public async Task Streaming_RetriesBeforeTheFirstChunk_WithoutDuplicatingText()
    {
        using var inner = new ScriptedClient(Fail(Refused()), Answer("resposta em partes"));
        using var client = Resilient(inner);

        var text = string.Concat(await client.GetStreamingResponseAsync("oi", cancellationToken: _token).Select(u => u.Text).ToListAsync(_token));

        Assert.Equal("resposta em partes ", text);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task UsageTracking_RecordsTokensAndTime_InBothModes()
    {
        var usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 };
        using var inner = new ScriptedClient(Answer("um"), Answer("dois três")) { Usage = usage };
        var tracker = new UsageTracker();
        using var client = new UsageTrackingChatClient(Resilient(inner), tracker, TimeProvider.System, NullLogger.Instance);

        await client.GetResponseAsync("oi", cancellationToken: _token);
        await client.GetStreamingResponseAsync("oi", cancellationToken: _token).ToListAsync(_token);

        Assert.Equal(2, tracker.Calls.Count);
        Assert.Equal(240, tracker.InputTokens);
        Assert.Equal(60, tracker.OutputTokens);
        Assert.All(tracker.Calls, call => Assert.True(call.Elapsed >= TimeSpan.Zero));
    }

    [Fact]
    public async Task ProviderWithoutUsage_IsRecordedWithNullTokens()
    {
        using var inner = new ScriptedClient(Answer("ok"));
        var tracker = new UsageTracker();
        using var client = new UsageTrackingChatClient(inner, tracker, TimeProvider.System, NullLogger.Instance);

        await client.GetResponseAsync("oi", cancellationToken: _token);

        Assert.Null(tracker.Last!.InputTokens);
        Assert.Equal(0, tracker.InputTokens);
    }

    [Fact]
    public void Pipeline_WrapsTheProvider_AndValidatesTheSettings()
    {
        var tracker = new UsageTracker();

        using var client = ChatClientFactory.CreatePipeline(
            new LlmOptions { Provider = LlmProvider.Fake }, _ => null, tracker, NullLoggerFactory.Instance);

        Assert.IsType<UsageTrackingChatClient>(client);
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.CreatePipeline(
            new LlmOptions { Provider = LlmProvider.Fake, MaxAttempts = 0 }, _ => null, tracker, NullLoggerFactory.Instance));
    }
}
