using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace Mimir.Infrastructure.Knowledge;

/// <summary>
/// Gerador de vetores sem modelo e sem rede, para testes e para o CI. Usado quando <c>Embeddings:Provider</c> é <c>Fake</c>.
/// </summary>
/// <remarks>
/// <para>
/// Usa feature hashing: cada palavra (sem acento e em minúsculas) cai numa posição do vetor pelo hash.
/// Textos com palavras em comum ficam próximos; sinônimos não ("férias" e "descanso" não se parecem).
/// É o que separa este gerador de um modelo de embeddings de verdade, que aprende o significado.
/// </para>
/// <para>O resultado é determinístico: o mesmo texto gera sempre o mesmo vetor, o que deixa os testes estáveis.</para>
/// </remarks>
public sealed class HashingEmbeddingGenerator(int dimensions = 256) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelName = "hashing-256";

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var embeddings = values.Select(text => new Embedding<float>(Vectorize(text))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    private float[] Vectorize(string text)
    {
        var vector = new float[dimensions];
        foreach (var word in Words(text))
        {
            var hash = Fnv1a(word);
            // O sinal também vem do hash, para que colisões tendam a se cancelar em vez de somar.
            vector[hash % (uint)dimensions] += (hash & 0x8000_0000) == 0 ? 1f : -1f;
        }

        return vector;
    }

    /// <summary>FNV-1a de 32 bits: simples, rápido e estável entre execuções (string.GetHashCode muda a cada processo).</summary>
    private static uint Fnv1a(string word)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(word))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    private static IEnumerable<string> Words(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length > 2);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
