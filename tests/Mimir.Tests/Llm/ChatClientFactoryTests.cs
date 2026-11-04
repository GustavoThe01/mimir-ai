using Mimir.Infrastructure.Llm;
using OllamaSharp;

namespace Mimir.Tests.Llm;

/// <summary>Nenhum destes testes faz chamada de rede: criar o cliente não contata o provedor.</summary>
public class ChatClientFactoryTests
{
    private static string? NoKeys(string variable) => null;

    private static string? FakeKeys(string variable) => "chave-de-teste";

    [Fact]
    public void DefaultProvider_IsTheFreeLocalOne()
    {
        Assert.Equal(LlmProvider.Ollama, new LlmOptions().Provider);
    }

    [Fact]
    public void Ollama_DoesNotNeedAnApiKey()
    {
        var options = new LlmOptions { Provider = LlmProvider.Ollama, Model = "modelo", Endpoint = "http://localhost:11434" };

        using var client = ChatClientFactory.Create(options, NoKeys);

        Assert.IsType<OllamaApiClient>(client);
    }

    [Fact]
    public void Ollama_RequiresEndpoint()
    {
        var options = new LlmOptions { Provider = LlmProvider.Ollama, Model = "modelo" };

        var exception = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(options, NoKeys));

        Assert.Contains("Llm:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LlmProvider.Anthropic, ChatClientFactory.AnthropicKeyVariable)]
    [InlineData(LlmProvider.OpenAI, ChatClientFactory.OpenAIKeyVariable)]
    public void PaidProviders_WithoutKey_ExplainWhichVariableIsMissing(LlmProvider provider, string variable)
    {
        var options = new LlmOptions { Provider = provider, Model = "modelo" };

        var exception = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(options, NoKeys));

        Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.OpenAI)]
    public void PaidProviders_WithKey_AreReady(LlmProvider provider)
    {
        var options = new LlmOptions { Provider = provider, Model = "modelo" };

        using var client = ChatClientFactory.Create(options, FakeKeys);

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(LlmProvider.Ollama)]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.OpenAI)]
    public void RealProviders_RequireModel(LlmProvider provider)
    {
        var options = new LlmOptions { Provider = provider, Endpoint = "http://localhost:11434" };

        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(options, FakeKeys));
    }

    [Fact]
    public void OpenAI_AcceptsCompatibleEndpoint()
    {
        var options = new LlmOptions { Provider = LlmProvider.OpenAI, Model = "modelo", Endpoint = "https://exemplo.test/v1" };

        using var client = ChatClientFactory.Create(options, FakeKeys);

        Assert.NotNull(client);
    }
}
