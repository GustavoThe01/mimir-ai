using System.Text.Json;
using Microsoft.Extensions.AI;
using Mimir.Core.Llm;

namespace Mimir.Tests.Llm;

public class TicketClassifierTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    private sealed class ScriptedChatClient(params string[] answers) : IChatClient
    {
        private readonly Queue<string> _answers = new(answers);

        public List<ChatMessage> LastMessages { get; private set; } = [];

        public ChatOptions? LastOptions { get; private set; }

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMessages = messages.ToList();
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _answers.Dequeue())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private const string Valid = """{"department": "TI", "reason": "O problema é no computador."}""";

    [Fact]
    public async Task ValidAnswer_BecomesATypedResult()
    {
        using var client = new ScriptedChatClient(Valid);

        var result = await new TicketClassifier(client).ClassifyAsync("Meu computador não liga.", _token);

        Assert.Equal(Department.Ti, result.Department);
        Assert.Equal("O problema é no computador.", result.Reason);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Request_SendsTheSchema_AndNoSamplingParameters()
    {
        using var client = new ScriptedChatClient(Valid);

        await new TicketClassifier(client).ClassifyAsync("Meu computador não liga.", _token);

        Assert.IsType<ChatResponseFormatJson>(client.LastOptions?.ResponseFormat);
        Assert.Null(client.LastOptions?.Temperature);
        Assert.Contains("<chamado>Meu computador não liga.</chamado>", client.LastMessages[^1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("""{"department": "MARKETING", "reason": "Setor inventado."}""")]
    [InlineData("""{"department": "TI"}""")]
    [InlineData("""{"department": "TI", "reason": " "}""")]
    [InlineData("""{"department": "TI", "reason": "ok", "extra": "campo a mais"}""")]
    [InlineData("""["TI"]""")]
    public async Task InvalidAnswer_GetsOneMoreChance(string invalid)
    {
        using var client = new ScriptedChatClient(invalid, Valid);

        var result = await new TicketClassifier(client).ClassifyAsync("Chamado", _token);

        Assert.Equal(Department.Ti, result.Department);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task InvalidTwice_FailsExplicitly_InsteadOfInventingAnAnswer()
    {
        using var client = new ScriptedChatClient(
            """{"department": "MARKETING", "reason": "x"}""",
            """{"department": "MARKETING", "reason": "x"}""");

        var exception = await Assert.ThrowsAsync<StructuredOutputException>(
            () => new TicketClassifier(client).ClassifyAsync("Chamado", _token));

        Assert.Contains("MARKETING", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task EmptyTicket_IsRejected_WithoutCallingTheModel()
    {
        using var client = new ScriptedChatClient(Valid);

        await Assert.ThrowsAsync<ArgumentException>(() => new TicketClassifier(client).ClassifyAsync(" ", _token));

        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void Prompts_AreLoadedFromTheAssembly()
    {
        Assert.Contains("<chamado>", PromptLibrary.Load(PromptLibrary.TicketClassification), StringComparison.Ordinal);
        Assert.Contains("Mimir", PromptLibrary.Load(PromptLibrary.Answer), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => PromptLibrary.Load("nao-existe.txt"));
    }

    [Fact]
    public void Validator_RefusesKeywordsItDoesNotImplement()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""{"type": "string", "pattern": "^a"}""");
        var value = JsonSerializer.Deserialize<JsonElement>("\"a\"");

        Assert.Throws<NotSupportedException>(() => JsonSchemaValidator.Validate(value, schema));
    }
}
