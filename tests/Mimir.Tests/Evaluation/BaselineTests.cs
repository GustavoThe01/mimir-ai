using System.Text.Json;
using Microsoft.Extensions.AI;
using Mimir.Core.Evaluation;
using Mimir.Core.Llm;

namespace Mimir.Tests.Evaluation;

/// <summary>Linha de base: exemplos no prompt (few-shot), escolha dos exemplos e F1 macro.</summary>
public class BaselineTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    private static readonly TicketSplits Splits = TicketSplits.Load(Path.Combine(AppContext.BaseDirectory, "datasets", "chamados"));

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> Messages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"department": "TI", "reason": "computador"}""")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task FewShot_SendsEachExampleAsAQuestionAndAnswer_BeforeTheRealTicket()
    {
        var chat = new RecordingChatClient();
        var classifier = new TicketClassifier(chat, [("A impressora não imprime.", Department.Ti), ("Quero tirar férias.", Department.Rh)]);

        await classifier.ClassifyAsync("Meu notebook não liga.", _token);

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User], chat.Messages.Select(m => m.Role));
        Assert.Equal("<chamado>A impressora não imprime.</chamado>", chat.Messages[1].Text);
        Assert.Equal("TI", JsonSerializer.Deserialize<JsonElement>(chat.Messages[2].Text).GetProperty("department").GetString());
        Assert.Equal("RH", JsonSerializer.Deserialize<JsonElement>(chat.Messages[4].Text).GetProperty("department").GetString());
        Assert.Equal("<chamado>Meu notebook não liga.</chamado>", chat.Messages[^1].Text);
    }

    [Fact]
    public async Task ZeroShot_HasOnlyTheSystemPromptAndTheTicket()
    {
        var chat = new RecordingChatClient();

        await new TicketClassifier(chat).ClassifyAsync("Meu notebook não liga.", _token);

        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public void FewShotExamples_ComeOnlyFromTraining_OnePerSituation_BalancedByDepartment()
    {
        var examples = Splits.FewShotExamples(perDepartment: 2);

        Assert.Equal(10, examples.Count);
        Assert.All(examples.GroupBy(e => e.Department), group => Assert.Equal(2, group.Count()));
        var trainTexts = Splits.Train.Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        Assert.All(examples, e => Assert.Contains(e.Ticket, trainTexts));
        var situations = examples.Select(e => Splits.Train.Single(t => t.Text == e.Ticket).Group).ToList();
        Assert.Equal(situations.Count, situations.Distinct().Count());
        Assert.Empty(Splits.FewShotExamples(perDepartment: 0));
    }

    [Fact]
    public void FewShotExamples_AreTheSameEveryTime()
    {
        Assert.Equal(Splits.FewShotExamples(2), Splits.FewShotExamples(2));
    }

    [Fact]
    public void MacroF1_GivesEachDepartmentTheSameWeight()
    {
        // TI acerta tudo; RH erra metade (vira OUTROS). Os setores sem casos contam com F1 zero.
        var results = new List<CaseResult>
        {
            new(new EvaluationCase("1", "a", Department.Ti), Department.Ti, null, TimeSpan.Zero),
            new(new EvaluationCase("2", "b", Department.Ti), Department.Ti, null, TimeSpan.Zero),
            new(new EvaluationCase("3", "c", Department.Rh), Department.Rh, null, TimeSpan.Zero),
            new(new EvaluationCase("4", "d", Department.Rh), Department.Outros, null, TimeSpan.Zero),
        };

        var report = new EvaluationReport("m", "p", DateTimeOffset.UnixEpoch, results);

        var expected = report.PerDepartment.Average(m => m.F1);
        Assert.Equal(expected, report.MacroF1, 6);
        Assert.Contains("| F1 macro |", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void TestSplit_BecomesEvaluationCases_WithTheSameLabels()
    {
        var cases = TicketSplits.AsCases(Splits.Test);

        Assert.Equal(60, cases.Count);
        Assert.Equal(Splits.Test.Select(t => t.Department), cases.Select(c => c.Expected));
    }
}
