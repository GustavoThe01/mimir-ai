using Microsoft.Extensions.AI;
using Mimir.Core.Evaluation;
using Mimir.Core.Llm;

namespace Mimir.Tests.Evaluation;

public class EvaluationTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    /// <summary>Responde conforme o chamado, para simular um modelo que acerta uns casos e erra outros.</summary>
    private sealed class RuleChatClient(Func<string, string> answer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(messages.Last().Text))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static string Json(string department) => $$"""{"department": "{{department}}", "reason": "Teste."}""";

    private static EvaluationCase Case(string id, Department expected) => new(id, "Chamado " + id, expected);

    private static CaseResult Result(string id, Department expected, Department? predicted) =>
        new(Case(id, expected), predicted, predicted is null ? "inválida" : null, TimeSpan.FromSeconds(2));

    [Fact]
    public void Dataset_ParsesJsonLines_SkippingBlankLines()
    {
        const string text = """
            {"id": "a", "ticket": "Meu notebook não liga.", "expected": "TI"}

            {"id": "b", "ticket": "Quero tirar férias.", "expected": "rh"}
            """;

        var cases = EvaluationDataset.Parse(text);

        Assert.Equal([new EvaluationCase("a", "Meu notebook não liga.", Department.Ti), new EvaluationCase("b", "Quero tirar férias.", Department.Rh)], cases);
    }

    [Theory]
    [InlineData("""{"id": "a", "ticket": "x", "expected": "MARKETING"}""", "não existe")]
    [InlineData("""{"id": "a", "ticket": "x", "expected": "2"}""", "não existe")]
    [InlineData("""{"id": "a", "expected": "TI"}""", "'ticket'")]
    [InlineData("{\"id\": \"a\", \"ticket\": \"x\", \"expected\": \"TI\"}\n{\"id\": \"a\", \"ticket\": \"y\", \"expected\": \"RH\"}", "repetido")]
    [InlineData("isto não é json", "JSON")]
    [InlineData("   ", "vazio")]
    public void Dataset_RejectsInvalidContent(string text, string message)
    {
        var exception = Assert.Throws<FormatException>(() => EvaluationDataset.Parse(text));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dataset_InTheRepository_IsValidAndBalanced()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "datasets", "classificacao-chamados.v1.jsonl");

        var cases = EvaluationDataset.Parse(File.ReadAllText(path));

        Assert.Equal(20, cases.Count);
        Assert.All(Enum.GetValues<Department>(), d => Assert.Equal(4, cases.Count(c => c.Expected == d)));
    }

    [Fact]
    public void Report_CalculatesAccuracyAndMetricsPerDepartment()
    {
        var report = new EvaluationReport("fake", "prompt.v1", DateTimeOffset.UnixEpoch,
        [
            Result("1", Department.Ti, Department.Ti),
            Result("2", Department.Ti, Department.Financeiro),
            Result("3", Department.Financeiro, Department.Financeiro),
            Result("4", Department.Rh, null),
        ]);

        Assert.Equal(2, report.Correct);
        Assert.Equal(0.5, report.Accuracy);
        Assert.Equal(1, report.InvalidOutputs);
        Assert.Equal(TimeSpan.FromSeconds(2), report.AverageLatency);

        var ti = report.PerDepartment.Single(m => m.Department == Department.Ti);
        Assert.Equal((2, 1.0, 0.5), (ti.Support, ti.Precision, ti.Recall));
        var financeiro = report.PerDepartment.Single(m => m.Department == Department.Financeiro);
        Assert.Equal((0.5, 1.0), (financeiro.Precision, financeiro.Recall));
        Assert.Equal(0, report.PerDepartment.Single(m => m.Department == Department.Juridico).F1);

        Assert.Equal(1, report.Count(Department.Ti, Department.Financeiro));
        Assert.Equal(1, report.Count(Department.Rh, null));
    }

    [Fact]
    public void Report_RendersMarkdownWithTheErrors()
    {
        var report = new EvaluationReport("Ollama · llama3.2:3b", "ticket-classification.v1.txt", DateTimeOffset.UnixEpoch,
        [
            Result("ti-01", Department.Ti, Department.Ti),
            Result("rh-02", Department.Rh, Department.Financeiro),
        ]);

        var markdown = report.ToMarkdown();

        Assert.Contains("**1 de 2 (50,0%)**", markdown, StringComparison.Ordinal);
        Assert.Contains("| TI | 1 | 100,0% | 100,0% | 100,0% |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `rh-02` | RH | FINANCEIRO |", markdown, StringComparison.Ordinal);
        // Colunas na ordem do enum: FINANCEIRO, TI, RH, JURIDICO, OUTROS e, por último, resposta inválida.
        Assert.Contains("| RH | 1 | 0 | 0 | 0 | 0 | 0 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_WithoutCases_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new EvaluationReport("fake", "p", DateTimeOffset.UnixEpoch, []));
    }

    [Fact]
    public async Task Evaluator_RunsEveryCase_AndCountsInvalidOutputsAsErrors()
    {
        using var client = new RuleChatClient(ticket =>
            ticket.Contains("ti-", StringComparison.Ordinal) ? Json("TI")
            : ticket.Contains("rh-", StringComparison.Ordinal) ? "isto não é json"
            : Json("OUTROS"));
        var evaluator = new ClassificationEvaluator(new TicketClassifier(client));
        var seen = new List<string>();

        var report = await evaluator.RunAsync(
            [Case("ti-01", Department.Ti), Case("rh-01", Department.Rh), Case("jur-01", Department.Juridico)],
            "fake",
            new Progress(r => seen.Add(r.Case.Id)),
            _token);

        Assert.Equal(["ti-01", "rh-01", "jur-01"], seen);
        Assert.Equal(1, report.Correct);
        Assert.Equal(1, report.InvalidOutputs);
        Assert.Equal(Department.Outros, report.Results[2].Predicted);
        Assert.Equal(PromptLibrary.TicketClassification, report.Prompt);
    }

    private sealed class Progress(Action<CaseResult> report) : IProgress<CaseResult>
    {
        public void Report(CaseResult value) => report(value);
    }
}
