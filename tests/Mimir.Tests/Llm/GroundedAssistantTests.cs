using System.Text.Json;
using Microsoft.Extensions.AI;
using Mimir.Core.Knowledge;
using Mimir.Core.Llm;

namespace Mimir.Tests.Llm;

public class GroundedAssistantTests
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

    /// <summary>Devolve os trechos que recebeu, sem filtrar perfil: simula uma busca com defeito.</summary>
    private sealed class FixedRetriever(params KnowledgeChunk[] chunks) : IChunkRetriever
    {
        public int? LastTop { get; private set; }

        public AccessScope? LastScope { get; private set; }

        public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string question, int top, AccessScope scope, CancellationToken cancellationToken)
        {
            LastTop = top;
            LastScope = scope;
            return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(chunks.Take(top).ToList());
        }
    }

    private static readonly AccessScope Anyone = AccessScope.Public;

    private static KnowledgeChunk Chunk(string document, string section, string text, int start, params string[] access) =>
        new($"{document}#{start}", document, "Política " + document, section, text, start, start + 2, Department.Rh,
            access.Length == 0 ? ["todos"] : access, "hash");

    private static readonly KnowledgeChunk Salaries = Chunk("faixas-salariais", "Engenharia",
        "Desenvolvimento pleno: de R$ 7.000 a R$ 10.500.", 14, "rh", "diretoria");

    private static readonly KnowledgeChunk Vacation = Chunk("politica-de-ferias", "Divisão em períodos",
        "As férias podem ser divididas em até três períodos, e um deles precisa ter pelo menos 14 dias.", 12);

    private static readonly KnowledgeChunk Remote = Chunk("trabalho-remoto", "Ajuda de custo",
        "Quem trabalha de casa recebe ajuda de custo mensal de R$ 150 para internet e energia.", 20);

    private static string Json(bool answered, string answer, params int[] sources) =>
        JsonSerializer.Serialize(new { answered, answer, sources });

    [Fact]
    public async Task Answer_CarriesTheCitationsOfTheChunksItUsed()
    {
        var chat = new ScriptedChatClient(Json(true, "Em até três períodos, um deles com pelo menos 14 dias [1].", 1));
        var assistant = new GroundedAssistant(chat, new FixedRetriever(Vacation, Remote));

        var answer = await assistant.AnswerAsync("posso dividir as férias?", Anyone, cancellationToken: _token);

        Assert.True(answer.Answered);
        Assert.Contains("14 dias", answer.Text, StringComparison.Ordinal);
        var source = Assert.Single(answer.Sources);
        Assert.Equal(1, source.Number);
        Assert.Equal("politica-de-ferias.md#L12-L14", source.Citation);
        Assert.Equal("Divisão em períodos", source.Section);
    }

    [Fact]
    public async Task Prompt_NumbersTheChunks_AndAsksForJsonWithTheSchema()
    {
        var chat = new ScriptedChatClient(Json(true, "R$ 150 por mês [2].", 2));
        var retriever = new FixedRetriever(Vacation, Remote);

        await new GroundedAssistant(chat, retriever).AnswerAsync("ajuda de custo", Anyone, topChunks: 2, cancellationToken: _token);

        var user = chat.LastMessages.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("<fonte numero=\"1\">\nDivisão em períodos\nAs férias", user, StringComparison.Ordinal);
        Assert.Contains("<fonte numero=\"2\">", user, StringComparison.Ordinal);
        Assert.EndsWith("<pergunta>ajuda de custo</pergunta>", user, StringComparison.Ordinal);
        Assert.Equal(2, retriever.LastTop);
        Assert.Equal(0f, chat.LastOptions?.Temperature);
        Assert.IsType<ChatResponseFormatJson>(chat.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task WithoutChunks_TheAnswerIsDontKnow_AndTheModelIsNotCalled()
    {
        var chat = new ScriptedChatClient();

        var answer = await new GroundedAssistant(chat, new FixedRetriever()).AnswerAsync("qual o cardápio do refeitório?", Anyone, cancellationToken: _token);

        Assert.False(answer.Answered);
        Assert.Equal(GroundedAnswer.DontKnow, answer.Text);
        Assert.Equal(NoAnswerReason.NoSourcesFound, answer.Reason);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task WhenTheModelSaysTheChunksDoNotAnswer_TheTextIsOursNotTheModels()
    {
        var chat = new ScriptedChatClient(Json(false, "Acho que o refeitório abre às 11h.", []));

        var answer = await new GroundedAssistant(chat, new FixedRetriever(Vacation)).AnswerAsync("que horas abre o refeitório?", Anyone, cancellationToken: _token);

        Assert.False(answer.Answered);
        Assert.Equal(GroundedAnswer.DontKnow, answer.Text);
        Assert.Equal(NoAnswerReason.SourcesDoNotAnswer, answer.Reason);
        Assert.Empty(answer.Sources);
    }

    [Theory]
    [InlineData("""{"answered": true, "answer": "São 14 dias.", "sources": []}""")]          // sem citação
    [InlineData("""{"answered": true, "answer": "São 14 dias [3].", "sources": [3]}""")]     // cita trecho que não foi enviado
    [InlineData("""{"answered": true, "answer": "", "sources": [1]}""")]                     // resposta vazia
    [InlineData("""São 14 dias, pode confiar.""")]                                           // não é JSON
    [InlineData("""{"answered": true, "answer": "São 14 dias.", "sources": [1], "x": 1}""")] // fora do schema
    public async Task AnswerWithoutAValidSource_IsRetried_ThenBecomesDontKnow(string invalid)
    {
        var chat = new ScriptedChatClient(invalid, invalid);

        var answer = await new GroundedAssistant(chat, new FixedRetriever(Vacation)).AnswerAsync("férias", Anyone, cancellationToken: _token);

        Assert.Equal(2, chat.Calls);
        Assert.False(answer.Answered);
        Assert.Equal(NoAnswerReason.UncitedAnswer, answer.Reason);
        Assert.DoesNotContain("14", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondAttempt_CanFixTheFirst()
    {
        var chat = new ScriptedChatClient(Json(true, "São 14 dias.", []), Json(true, "Pelo menos 14 dias [1].", 1));

        var answer = await new GroundedAssistant(chat, new FixedRetriever(Vacation)).AnswerAsync("férias", Anyone, cancellationToken: _token);

        Assert.True(answer.Answered);
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task RepeatedSourceNumbers_AreListedOnce_InOrder()
    {
        var chat = new ScriptedChatClient(Json(true, "Férias [2] e ajuda de custo [1].", 2, 1, 2));

        var answer = await new GroundedAssistant(chat, new FixedRetriever(Remote, Vacation)).AnswerAsync("benefícios", Anyone, cancellationToken: _token);

        Assert.Equal([1, 2], answer.Sources.Select(s => s.Number));
        Assert.Equal("trabalho-remoto.md#L20-L22", answer.Sources[0].Citation);
    }

    [Fact]
    public async Task RestrictedChunk_NeverReachesTheModel_EvenIfTheSearchReturnsIt()
    {
        var chat = new ScriptedChatClient(Json(true, "Pelo menos 14 dias [1].", 1));
        var retriever = new FixedRetriever(Salaries, Vacation);

        var answer = await new GroundedAssistant(chat, retriever).AnswerAsync("salário e férias", Anyone, cancellationToken: _token);

        var prompt = string.Join("\n", chat.LastMessages.Select(m => m.Text));
        Assert.DoesNotContain("10.500", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("faixas-salariais", prompt, StringComparison.Ordinal);
        Assert.Contains("14 dias", prompt, StringComparison.Ordinal);
        Assert.Equal("politica-de-ferias.md#L12-L14", Assert.Single(answer.Sources).Citation);
        Assert.Same(Anyone, retriever.LastScope);
    }

    [Fact]
    public async Task WhenEveryChunkIsRestricted_TheAnswerIsDontKnow_WithoutCallingTheModel()
    {
        var chat = new ScriptedChatClient();

        var answer = await new GroundedAssistant(chat, new FixedRetriever(Salaries)).AnswerAsync("quanto ganha um pleno?", Anyone, cancellationToken: _token);

        Assert.Equal(NoAnswerReason.NoSourcesFound, answer.Reason);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task WithTheRightProfile_TheRestrictedChunkIsUsed()
    {
        var chat = new ScriptedChatClient(Json(true, "De R$ 7.000 a R$ 10.500 [1].", 1));

        var answer = await new GroundedAssistant(chat, new FixedRetriever(Salaries)).AnswerAsync("quanto ganha um pleno?", AccessScope.For("rh"), cancellationToken: _token);

        Assert.True(answer.Answered);
        Assert.Contains("10.500", chat.LastMessages.Single(m => m.Role == ChatRole.User).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidator_ChecksBooleanIntegerAndList()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(PromptLibrary.Load(PromptLibrary.GroundedAnswerSchema));

        var valid = JsonSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>(Json(true, "ok", 1, 2)), schema);
        var invalid = JsonSchemaValidator.Validate(
            JsonSerializer.Deserialize<JsonElement>("""{"answered": "sim", "answer": "ok", "sources": [0, "1"]}"""), schema);

        Assert.Empty(valid);
        Assert.Contains("$.answered: esperava verdadeiro ou falso.", invalid);
        Assert.Contains("$.sources[0]: 0 é menor que o mínimo 1.", invalid);
        Assert.Contains("$.sources[1]: esperava um número inteiro.", invalid);
    }
}
