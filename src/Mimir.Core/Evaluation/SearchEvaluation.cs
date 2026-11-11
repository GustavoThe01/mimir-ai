using System.Text.Json;
using Mimir.Core.Knowledge;

namespace Mimir.Core.Evaluation;

/// <summary>Uma pergunta e o documento que deveria aparecer no topo da busca.</summary>
public sealed record SearchCase(string Id, string Question, string ExpectedDocument, string Kind);

/// <summary>Métricas de uma forma de busca sobre o conjunto inteiro.</summary>
/// <param name="HitAt1">Em quantos casos o primeiro resultado é do documento esperado.</param>
/// <param name="HitAt3">Em quantos o documento esperado aparece entre os 3 primeiros.</param>
/// <param name="Mrr">Média de 1/posição do primeiro resultado certo (0 quando não aparece entre os 5 primeiros).</param>
public sealed record SearchScore(SearchMode Mode, int Cases, int HitAt1, int HitAt3, double Mrr, IReadOnlyList<string> Missed);

/// <summary>Compara as três formas de busca nas mesmas perguntas.</summary>
public static class SearchEvaluation
{
    public const int Depth = 5;

    public static IReadOnlyList<SearchCase> Parse(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        var cases = jsonLines.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select((line, index) =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(line);
                string Field(string name) => json.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                    ? text
                    : throw new FormatException($"Linha {index + 1}: o campo '{name}' é obrigatório.");
                return new SearchCase(Field("id"), Field("pergunta"), Field("documento"), Field("tipo"));
            })
            .ToList();

        return cases.Count > 0 ? cases : throw new FormatException("O conjunto de busca está vazio.");
    }

    public static async Task<IReadOnlyList<SearchScore>> RunAsync(
        HybridSearch search,
        IReadOnlyList<SearchCase> cases,
        CancellationToken cancellationToken = default)
    {
        var scores = new List<SearchScore>();
        foreach (var mode in Enum.GetValues<SearchMode>())
        {
            var positions = new List<(SearchCase Case, int Position)>();
            foreach (var item in cases)
            {
                var hits = await search.SearchAsync(item.Question, mode, Depth, cancellationToken);
                var index = hits.ToList().FindIndex(hit => hit.Chunk.DocumentId == item.ExpectedDocument);
                positions.Add((item, index < 0 ? 0 : index + 1));
            }

            scores.Add(Score(mode, positions));
        }

        return scores;
    }

    public static SearchScore Score(SearchMode mode, IReadOnlyList<(SearchCase Case, int Position)> positions) => new(
        mode,
        positions.Count,
        positions.Count(p => p.Position == 1),
        positions.Count(p => p.Position is >= 1 and <= 3),
        positions.Average(p => p.Position == 0 ? 0 : 1.0 / p.Position),
        [.. positions.Where(p => p.Position != 1).Select(p => p.Case.Id)]);
}
