using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Mimir.Core.Knowledge;

namespace Mimir.Core.Llm;

/// <summary>Por que não houve resposta.</summary>
public enum NoAnswerReason
{
    /// <summary>A busca não trouxe nenhum trecho. O modelo nem é chamado.</summary>
    NoSourcesFound,

    /// <summary>O modelo leu os trechos e disse que eles não respondem à pergunta.</summary>
    SourcesDoNotAnswer,

    /// <summary>O modelo tentou responder sem citar um trecho válido, duas vezes. A resposta é descartada.</summary>
    UncitedAnswer,
}

/// <summary>Um trecho citado na resposta: o número usado no texto e onde ele está.</summary>
public sealed record CitedSource(int Number, string Citation, string Title, string Section);

/// <summary>
/// Resposta com fonte. Quando <see cref="Answered"/> é falso, <see cref="Text"/> é sempre a mesma frase de
/// "não sei", e nunca um texto do modelo.
/// </summary>
public sealed record GroundedAnswer(bool Answered, string Text, IReadOnlyList<CitedSource> Sources, NoAnswerReason? Reason)
{
    public const string DontKnow = "Não sei. Não encontrei nos documentos da base um trecho que responda a essa pergunta.";

    public static GroundedAnswer NotFound(NoAnswerReason reason) => new(false, DontKnow, [], reason);
}

/// <summary>Onde o assistente busca os trechos. Na prática, a <see cref="HybridSearch"/>.</summary>
public interface IChunkRetriever
{
    Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken);
}

public sealed class HybridRetriever(HybridSearch search) : IChunkRetriever
{
    public async Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken) =>
        [.. (await search.SearchAsync(question, SearchMode.Hybrid, top, cancellationToken)).Select(hit => hit.Chunk)];
}

/// <summary>
/// RAG: busca os trechos, monta o prompt com eles numerados e exige que a resposta cite os trechos usados.
/// </summary>
/// <remarks>
/// <para>A garantia não depende de o modelo obedecer ao prompt. O código confere a resposta:</para>
/// <list type="bullet">
///   <item>sem trecho encontrado, a resposta é "não sei" e o modelo nem é chamado (custo zero);</item>
///   <item>resposta que não cita nenhum trecho, ou cita um número que não foi enviado, ganha uma nova tentativa;</item>
///   <item>se errar de novo, vira "não sei". Uma resposta sem fonte nunca chega a quem perguntou.</item>
/// </list>
/// </remarks>
public sealed class GroundedAssistant(IChatClient chatClient, IChunkRetriever retriever)
{
    public const int DefaultTopChunks = 4;
    private const int MaxAttempts = 2;

    private static readonly string SystemPrompt = PromptLibrary.Load(PromptLibrary.GroundedAnswer);
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>(
        PromptLibrary.Load(PromptLibrary.GroundedAnswerSchema));

    public async Task<GroundedAnswer> AnswerAsync(string question, int topChunks = DefaultTopChunks, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentOutOfRangeException.ThrowIfLessThan(topChunks, 1);

        var chunks = await retriever.RetrieveAsync(question.Trim(), topChunks, cancellationToken);
        if (chunks.Count == 0)
        {
            return GroundedAnswer.NotFound(NoAnswerReason.NoSourcesFound);
        }

        ChatMessage[] messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, BuildUserMessage(question.Trim(), chunks)),
        ];
        var options = new ChatOptions
        {
            MaxOutputTokens = 512,
            Temperature = 0,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "grounded_answer"),
        };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
            var parsed = Parse(response.Text, chunks);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return GroundedAnswer.NotFound(NoAnswerReason.UncitedAnswer);
    }

    /// <summary>A pergunta e os trechos numerados, cada um com o caminho da seção (que começa pelo título do documento).</summary>
    public static string BuildUserMessage(string question, IReadOnlyList<KnowledgeChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var builder = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            builder.Append(CultureInfo.InvariantCulture, $"<fonte numero=\"{i + 1}\">\n{chunk.Section}\n{chunk.Text}\n</fonte>\n\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"<pergunta>{question}</pergunta>");
        return builder.ToString();
    }

    /// <returns>A resposta válida, ou <c>null</c> quando o modelo errou o formato ou não citou um trecho válido.</returns>
    private static GroundedAnswer? Parse(string text, IReadOnlyList<KnowledgeChunk> chunks)
    {
        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (JsonSchemaValidator.Validate(json, Schema).Count > 0)
        {
            return null;
        }

        if (!json.GetProperty("answered").GetBoolean())
        {
            return GroundedAnswer.NotFound(NoAnswerReason.SourcesDoNotAnswer);
        }

        var answer = json.GetProperty("answer").GetString()!.Trim();
        var numbers = json.GetProperty("sources").EnumerateArray().Select(item => item.GetInt32()).Distinct().Order().ToList();
        if (answer.Length == 0 || numbers.Count == 0 || numbers.Any(number => number > chunks.Count))
        {
            return null;
        }

        var sources = numbers
            .Select(number => chunks[number - 1])
            .Select((chunk, index) => new CitedSource(numbers[index], chunk.Citation, chunk.Title, chunk.Section))
            .ToList();
        return new GroundedAnswer(true, answer, sources, null);
    }
}
