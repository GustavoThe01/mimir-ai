using Microsoft.Extensions.AI;

namespace Mimir.Core.Knowledge;

public enum SearchMode
{
    /// <summary>Só por significado (vetores).</summary>
    Vector = 1,

    /// <summary>Só pelos termos (FTS5).</summary>
    Text = 2,

    /// <summary>As duas, com os rankings fundidos.</summary>
    Hybrid = 3,
}

/// <summary>Resultado da busca. As posições dizem de onde o trecho veio: 1 é o primeiro daquela busca, nulo é "não apareceu".</summary>
public sealed record HybridHit(KnowledgeChunk Chunk, double Score, int? VectorRank, int? TextRank);

/// <summary>
/// Busca híbrida: roda a busca por significado e a busca por termos, e funde os dois rankings com
/// Reciprocal Rank Fusion (RRF).
/// </summary>
/// <remarks>
/// <para>
/// Cada busca acerta onde a outra erra. A de vetores entende "descansar no fim do ano" como férias, mas pode perder um
/// termo exato como "LGPD" ou "48 horas". A de termos acha o termo exato, mas não sabe que "dinheiro de volta" é reembolso.
/// </para>
/// <para>
/// O RRF soma <c>1 / (k + posição)</c> de cada lista. Ele usa só a posição, e não a nota: o cosseno e o bm25 estão em
/// escalas diferentes, e somar as notas direto favoreceria uma das buscas sem motivo. Com k = 60 (o valor do artigo
/// original), um trecho que aparece bem nas duas listas passa à frente de um que só lidera uma delas.
/// </para>
/// </remarks>
public sealed class HybridSearch(IEmbeddingGenerator<string, Embedding<float>> generator, IChunkStore store, string model)
{
    public const int RrfK = 60;
    private const int CandidatesPerSearch = 20;

    /// <summary>Busca em todos os trechos, sem filtro de perfil. Para diagnóstico (<c>mimir buscar</c>) e avaliação da busca.</summary>
    public Task<IReadOnlyList<HybridHit>> SearchAsync(
        string query,
        SearchMode mode = SearchMode.Hybrid,
        int top = 5,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, mode, top, scope: null, cancellationToken);

    /// <summary>
    /// Busca só entre os trechos que o escopo pode ler. O filtro vem antes do ranking: um documento restrito não
    /// ocupa vaga no resultado de quem não pode lê-lo.
    /// </summary>
    public async Task<IReadOnlyList<HybridHit>> SearchAsync(
        string query,
        SearchMode mode,
        int top,
        AccessScope? scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);

        var all = (await store.GetAllAsync(cancellationToken))
            .Where(item => scope is null || scope.CanRead(item.Chunk))
            .ToDictionary(item => item.Chunk.Id, item => item.Chunk, StringComparer.Ordinal);

        IReadOnlyList<string> vector = mode == SearchMode.Text
            ? []
            : [.. (await new SemanticSearch(generator, store, model).SearchAsync(query, CandidatesPerSearch, scope, cancellationToken)).Select(hit => hit.Chunk.Id)];
        // A busca por texto roda no SQLite, sem saber de perfis: pede mais candidatos e descarta o que o escopo não lê.
        IReadOnlyList<string> text = mode == SearchMode.Vector
            ? []
            : [.. (await store.SearchTextAsync(query, scope is null ? CandidatesPerSearch : CandidatesPerSearch * 5, cancellationToken))
                .Where(all.ContainsKey)
                .Take(CandidatesPerSearch)];

        var vectorRank = Ranks(vector);
        var textRank = Ranks(text);

        return vector.Concat(text)
            .Distinct(StringComparer.Ordinal)
            .Where(all.ContainsKey)
            .Select(id => new HybridHit(
                all[id],
                Rrf(vectorRank.GetValueOrDefault(id)) + Rrf(textRank.GetValueOrDefault(id)),
                vectorRank.TryGetValue(id, out var v) ? v : null,
                textRank.TryGetValue(id, out var t) ? t : null))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Chunk.Id, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    private static Dictionary<string, int> Ranks(IReadOnlyList<string> ids) =>
        ids.Select((id, index) => (id, rank: index + 1)).ToDictionary(item => item.id, item => item.rank, StringComparer.Ordinal);

    private static double Rrf(int rank) => rank == 0 ? 0 : 1.0 / (RrfK + rank);
}
