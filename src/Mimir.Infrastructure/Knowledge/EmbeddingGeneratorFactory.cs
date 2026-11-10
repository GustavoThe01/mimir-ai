using System.ClientModel;
using Microsoft.Extensions.AI;
using Mimir.Infrastructure.Llm;
using OllamaSharp;
using OpenAI;

namespace Mimir.Infrastructure.Knowledge;

/// <summary>Seção "Embeddings" da configuração. Separada de "Llm": o modelo que conversa não é o que gera vetores.</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>Ollama (padrão, grátis), OpenAI (pago) ou Fake (sem modelo, para testes e CI).</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    public string? Model { get; set; }

    public string? Endpoint { get; set; }

    /// <summary>Arquivo SQLite do índice de trechos.</summary>
    public string Database { get; set; } = "mimir.db";
}

/// <summary>O único lugar que conhece os provedores de embeddings.</summary>
/// <remarks>
/// A Anthropic não oferece um modelo de embeddings próprio; por isso não há opção Anthropic aqui.
/// As alternativas pagas estão no README, na seção sobre APIs pagas.
/// </remarks>
public static class EmbeddingGeneratorFactory
{
    public static (IEmbeddingGenerator<string, Embedding<float>> Generator, string Model) Create(
        EmbeddingOptions options,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        switch (options.Provider)
        {
            case LlmProvider.Fake:
                return (new HashingEmbeddingGenerator(), HashingEmbeddingGenerator.ModelName);

            case LlmProvider.Ollama:
                if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
                {
                    throw new InvalidOperationException("Configure Embeddings:Endpoint com o endereço do Ollama.");
                }

                var ollamaModel = RequiredModel(options);
                return (new OllamaApiClient(endpoint, ollamaModel), $"ollama/{ollamaModel}");

            case LlmProvider.OpenAI:
                var key = readEnvironmentVariable(ChatClientFactory.OpenAIKeyVariable);
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new InvalidOperationException($"Defina a variável de ambiente {ChatClientFactory.OpenAIKeyVariable} para usar este provedor.");
                }

                var clientOptions = new OpenAIClientOptions();
                if (!string.IsNullOrWhiteSpace(options.Endpoint))
                {
                    clientOptions.Endpoint = new Uri(options.Endpoint);
                }

                var openAIModel = RequiredModel(options);
                var generator = new OpenAIClient(new ApiKeyCredential(key), clientOptions)
                    .GetEmbeddingClient(openAIModel)
                    .AsIEmbeddingGenerator();
                return (generator, $"openai/{openAIModel}");

            default:
                throw new InvalidOperationException(
                    $"O provedor {options.Provider} não gera embeddings. Use Ollama, OpenAI ou Fake.");
        }
    }

    private static string RequiredModel(EmbeddingOptions options) =>
        string.IsNullOrWhiteSpace(options.Model)
            ? throw new InvalidOperationException($"Configure Embeddings:Model para o provedor {options.Provider}.")
            : options.Model;
}
