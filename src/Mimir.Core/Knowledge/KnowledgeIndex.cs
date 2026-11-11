using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Mimir.Core.Knowledge;

/// <summary>Um trecho com o vetor dele e o modelo que gerou o vetor.</summary>
public sealed record StoredChunk(KnowledgeChunk Chunk, ReadOnlyMemory<float> Vector, string Model);

/// <summary>O que identifica a versão de um trecho já indexado: o texto (pelo hash) e o modelo do vetor.</summary>
public sealed record ChunkFingerprint(string Hash, string Model);

/// <summary>Porta de saída: onde os trechos e os vetores ficam guardados.</summary>
public interface IChunkStore
{
    Task<IReadOnlyDictionary<string, ChunkFingerprint>> GetFingerprintsAsync(CancellationToken cancellationToken);

    Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredChunk>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Busca textual pelos termos da pergunta, do mais relevante para o menos. Encontra códigos, siglas e números
    /// exatos, que a busca por significado costuma perder.
    /// </summary>
    Task<IReadOnlyList<string>> SearchTextAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>Resultado de uma indexação.</summary>
public sealed record IndexReport(int Embedded, int Unchanged, int Removed, string Model, int Dimensions, TimeSpan Elapsed);

/// <summary>Um trecho encontrado e a similaridade com a pergunta (cosseno, de -1 a 1).</summary>
public sealed record SearchHit(KnowledgeChunk Chunk, double Score);

/// <summary>
/// Gera os vetores dos trechos e guarda no índice. A indexação é incremental: trecho com o mesmo texto e o mesmo
/// modelo não é enviado de novo ao gerador, o que poupa tempo no modelo local e dinheiro numa API paga.
/// </summary>
public sealed class KnowledgeIndexer(IEmbeddingGenerator<string, Embedding<float>> generator, IChunkStore store, string model)
{
    private const int BatchSize = 16;

    public async Task<IndexReport> IndexAsync(IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var start = Stopwatch.GetTimestamp();
        var existing = await store.GetFingerprintsAsync(cancellationToken);

        var pending = chunks
            .Where(chunk => !existing.TryGetValue(chunk.Id, out var stored) || stored != new ChunkFingerprint(chunk.Hash, model))
            .ToList();

        var dimensions = 0;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            // O texto com título e seção na frente: o vetor fica sabendo do que o trecho fala.
            var embeddings = await generator.GenerateAsync(batch.Select(chunk => chunk.ContextualText), cancellationToken: cancellationToken);
            if (embeddings.Count != batch.Length)
            {
                throw new InvalidOperationException($"O gerador devolveu {embeddings.Count} vetores para {batch.Length} trechos.");
            }

            dimensions = embeddings[0].Vector.Length;
            await store.UpsertAsync(
                [.. batch.Select((chunk, index) => new StoredChunk(chunk, embeddings[index].Vector, model))],
                cancellationToken);
        }

        var current = chunks.Select(chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
        var removed = existing.Keys.Where(id => !current.Contains(id)).ToList();
        await store.DeleteAsync(removed, cancellationToken);

        return new IndexReport(pending.Count, chunks.Count - pending.Count, removed.Count, model, dimensions, Stopwatch.GetElapsedTime(start));
    }
}

/// <summary>
/// Busca por significado: gera o vetor da pergunta e compara com todos os trechos pelo cosseno.
/// </summary>
/// <remarks>
/// Comparar com todos os vetores (força bruta) é exato e, com dezenas ou alguns milhares de trechos, rápido.
/// Para milhões de trechos, o caminho seria um índice aproximado (HNSW, sqlite-vec). A busca híbrida entra no Dia 08.
/// </remarks>
public sealed class SemanticSearch(IEmbeddingGenerator<string, Embedding<float>> generator, IChunkStore store, string model)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int top = 5, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);

        var stored = await store.GetAllAsync(cancellationToken);
        var comparable = stored.Where(item => item.Model == model).ToList();
        if (comparable.Count == 0)
        {
            throw new InvalidOperationException($"O índice não tem trechos do modelo {model}. Rode mimir ingerir antes de buscar.");
        }

        // Vetores de modelos diferentes não são comparáveis: cada modelo tem o próprio espaço.
        var question = await generator.GenerateVectorAsync(query.Trim(), cancellationToken: cancellationToken);
        return comparable
            .Select(item => new SearchHit(item.Chunk, VectorMath.Cosine(question.Span, item.Vector.Span)))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Chunk.Id, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }
}

public static class VectorMath
{
    /// <summary>Cosseno entre dois vetores: 1 é a mesma direção, 0 é sem relação. Vetor zero dá 0.</summary>
    public static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException($"Vetores de tamanhos diferentes: {left.Length} e {right.Length}.");
        }

        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
