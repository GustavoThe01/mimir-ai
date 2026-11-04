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
