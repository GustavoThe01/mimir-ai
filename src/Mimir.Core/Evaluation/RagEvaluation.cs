using System.Globalization;
using System.Text.Json;
using Mimir.Core.Knowledge;
using Mimir.Core.Llm;

namespace Mimir.Core.Evaluation;

/// <summary>
/// Uma pergunta do RAG com o resultado esperado. Sem documento esperado, o certo é "não sei": ou a base não fala do
/// assunto, ou fala, mas num documento que os perfis de quem pergunta não podem ler.
/// </summary>
public sealed record RagCase(string Id, string Question, IReadOnlyList<string> Profiles, string? ExpectedDocument, string? ExpectedFact);

public enum RagOutcome
{
    /// <summary>Respondeu, citou o documento esperado e a resposta tem o fato esperado.</summary>
    Correct,

    /// <summary>Disse "não sei" quando devia.</summary>
    CorrectDontKnow,

    /// <summary>Citou o documento esperado, mas a resposta não tem o fato esperado.</summary>
    MissingFact,

    /// <summary>Respondeu citando outro documento.</summary>
    WrongSource,

    /// <summary>Disse "não sei", mas a resposta estava num documento que a pessoa pode ler.</summary>
    FalseDontKnow,

    /// <summary>Respondeu quando devia dizer "não sei".</summary>
    ShouldNotAnswer,

    /// <summary>Citou um documento que os perfis de quem perguntou não podem ler. Não pode acontecer nunca.</summary>
    Leak,
}

public sealed record RagCaseResult(RagCase Case, RagOutcome Outcome, GroundedAnswer Answer, TimeSpan Elapsed)
{
    public bool IsCorrect => Outcome is RagOutcome.Correct or RagOutcome.CorrectDontKnow;
}

/// <summary>Roda o RAG de ponta a ponta (busca com permissão, modelo e conferência da citação) sobre um conjunto.</summary>
public static class RagEvaluation
{
    public static IReadOnlyList<RagCase> Parse(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        var cases = jsonLines.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select((line, index) =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(line);
                string Required(string name) => json.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                    ? text
                    : throw new FormatException($"Linha {index + 1}: o campo '{name}' é obrigatório.");
                string? Optional(string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
                var profiles = json.TryGetProperty("perfis", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(item => item.GetString()!).ToList()
                    : throw new FormatException($"Linha {index + 1}: o campo 'perfis' é obrigatório (lista, pode ser vazia).");
                return new RagCase(Required("id"), Required("pergunta"), profiles, Optional("documento"), Optional("fato"));
            })
            .ToList();

        return cases.Count > 0 ? cases : throw new FormatException("O conjunto do RAG está vazio.");
    }

    public static async Task<IReadOnlyList<RagCaseResult>> RunAsync(
        GroundedAssistant assistant,
        IReadOnlyList<RagCase> cases,
        Func<string, AccessScope, bool> canRead,
        IProgress<RagCaseResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(canRead);

        var results = new List<RagCaseResult>();
        foreach (var item in cases)
        {
            var scope = AccessScope.For([.. item.Profiles]);
            var started = DateTimeOffset.UtcNow;
            var answer = await assistant.AnswerAsync(item.Question, scope, cancellationToken: cancellationToken);
            var result = new RagCaseResult(item, Classify(item, answer, document => canRead(document, scope)), answer, DateTimeOffset.UtcNow - started);
            results.Add(result);
            progress?.Report(result);
        }

        return results;
    }

    /// <param name="canRead">Se o documento citado pode ser lido por quem perguntou. Usado só para detectar vazamento.</param>
    public static RagOutcome Classify(RagCase item, GroundedAnswer answer, Func<string, bool> canRead)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(canRead);

        var cited = answer.Sources.Select(source => DocumentOf(source.Citation)).Distinct(StringComparer.Ordinal).ToList();
        if (cited.Any(document => !canRead(document)))
        {
            return RagOutcome.Leak;
        }

        if (item.ExpectedDocument is null)
        {
            return answer.Answered ? RagOutcome.ShouldNotAnswer : RagOutcome.CorrectDontKnow;
        }

        if (!answer.Answered)
        {
            return RagOutcome.FalseDontKnow;
        }

        if (!cited.Contains(item.ExpectedDocument, StringComparer.Ordinal))
        {
            return RagOutcome.WrongSource;
        }

        return item.ExpectedFact is null || Normalize(answer.Text).Contains(Normalize(item.ExpectedFact), StringComparison.Ordinal)
            ? RagOutcome.Correct
            : RagOutcome.MissingFact;
    }

    /// <summary>A citação é <c>documento.md#L10-L12</c>; o documento é o que vem antes de <c>.md</c>.</summary>
    public static string DocumentOf(string citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        var end = citation.IndexOf(".md", StringComparison.Ordinal);
        return end < 0 ? citation : citation[..end];
    }

    private static string Normalize(string text) => text.ToLower(CultureInfo.GetCultureInfo("pt-BR")).Replace(" ", string.Empty, StringComparison.Ordinal);
}
