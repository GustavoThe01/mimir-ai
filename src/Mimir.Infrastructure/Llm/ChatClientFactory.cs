using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace Mimir.Infrastructure.Llm;

/// <summary>
/// O único lugar do projeto que conhece os provedores: transforma a configuração em um <see cref="IChatClient"/>.
/// </summary>
/// <remarks>Criar o cliente não faz nenhuma chamada de rede, e portanto não gera custo.</remarks>
public static class ChatClientFactory
{
    public const string AnthropicKeyVariable = "ANTHROPIC_API_KEY";
    public const string OpenAIKeyVariable = "OPENAI_API_KEY";

    public static IChatClient Create(LlmOptions options, Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        return options.Provider switch
        {
            LlmProvider.Ollama => CreateOllama(options),
            LlmProvider.Anthropic => CreateAnthropic(options, readEnvironmentVariable),
            LlmProvider.OpenAI => CreateOpenAI(options, readEnvironmentVariable),
            LlmProvider.Fake => new FakeChatClient(),
            _ => throw new InvalidOperationException($"Provedor de LLM desconhecido: {options.Provider}."),
        };
    }

    private static OllamaApiClient CreateOllama(LlmOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("Configure Llm:Endpoint com o endereço do Ollama.");
        }

        return new OllamaApiClient(endpoint, RequiredModel(options));
    }

    private static IChatClient CreateAnthropic(LlmOptions options, Func<string, string?> readEnvironmentVariable)
    {
        var client = new AnthropicClient { ApiKey = RequiredKey(AnthropicKeyVariable, readEnvironmentVariable) };
        return client.AsIChatClient(RequiredModel(options));
    }

    private static IChatClient CreateOpenAI(LlmOptions options, Func<string, string?> readEnvironmentVariable)
    {
        var credential = new ApiKeyCredential(RequiredKey(OpenAIKeyVariable, readEnvironmentVariable));
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(options.Endpoint);
        }

        return new OpenAIClient(credential, clientOptions).GetChatClient(RequiredModel(options)).AsIChatClient();
    }

    private static string RequiredModel(LlmOptions options) =>
        string.IsNullOrWhiteSpace(options.Model)
            ? throw new InvalidOperationException($"Configure Llm:Model para o provedor {options.Provider}.")
            : options.Model;

    private static string RequiredKey(string variable, Func<string, string?> readEnvironmentVariable)
    {
        var key = readEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException($"Defina a variável de ambiente {variable} para usar este provedor.")
            : key;
    }
}
