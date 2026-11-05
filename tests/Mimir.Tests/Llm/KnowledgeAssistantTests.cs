using Mimir.Core.Llm;
using Mimir.Infrastructure.Llm;

namespace Mimir.Tests.Llm;

public class KnowledgeAssistantTests
{
    [Fact]
    public async Task AskAsync_SendsTheQuestionToTheConfiguredClient()
    {
        using var client = new FakeChatClient();
        var assistant = new KnowledgeAssistant(client);

        var answer = await assistant.AskAsync("  Quantos dias de férias?  ", TestContext.Current.CancellationToken);

        Assert.Equal("[fake] Quantos dias de férias?", answer);
    }

    [Fact]
    public async Task AskStreamingAsync_DeliversTheAnswerInParts()
    {
        using var client = new FakeChatClient();
        var assistant = new KnowledgeAssistant(client);
        var parts = new List<string>();

        await foreach (var part in assistant.AskStreamingAsync("Quantos dias de férias?", TestContext.Current.CancellationToken))
        {
            parts.Add(part);
        }

        Assert.True(parts.Count > 1);
        Assert.Equal("[fake] Quantos dias de férias?", string.Concat(parts));
    }

    [Fact]
    public void AskStreamingAsync_RejectsEmptyQuestion_AtTheCall()
    {
        using var client = new FakeChatClient();
        var assistant = new KnowledgeAssistant(client);

        Assert.Throws<ArgumentException>(() => assistant.AskStreamingAsync(" ", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AskAsync_RejectsEmptyQuestion(string question)
    {
        using var client = new FakeChatClient();
        var assistant = new KnowledgeAssistant(client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => assistant.AskAsync(question, TestContext.Current.CancellationToken));
    }
}
