using Microsoft.Extensions.AI;
using Mimir.Core.Knowledge;
using Mimir.Infrastructure.Knowledge;
using Mimir.Infrastructure.Llm;

namespace Mimir.Tests.Knowledge;

public sealed class IndexTests : IDisposable
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"mimir-indice-{Guid.NewGuid():N}.db");

    public void Dispose() => File.Delete(_database);

    /// <summary>Conta quantos textos foram enviados ao gerador, para provar a indexação incremental.</summary>
    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly HashingEmbeddingGenerator _inner = new();

        public int Texts { get; private set; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Texts += list.Count;
            return await _inner.GenerateAsync(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _inner.Dispose();
    }

    private static async Task<List<KnowledgeChunk>> RepositoryChunksAsync(CancellationToken token)
    {
        var documents = await FileDocumentSource.LoadAsync(Path.Combine(AppContext.BaseDirectory, "knowledge"), token);
        return [.. documents.SelectMany(d => Chunker.Split(d))];
    }

    [Fact]
    public async Task Hashing_IsDeterministic_AndIgnoresAccentsAndCase()
    {
        using var generator = new HashingEmbeddingGenerator();

        var vectors = await generator.GenerateAsync(["Política de FÉRIAS", "politica de ferias", "senha bloqueada"], cancellationToken: _token);

        Assert.Equal(1.0, VectorMath.Cosine(vectors[0].Vector.Span, vectors[1].Vector.Span), precision: 6);
        Assert.True(VectorMath.Cosine(vectors[0].Vector.Span, vectors[2].Vector.Span) < 0.5);
        Assert.Equal(256, vectors[0].Vector.Length);
    }

    [Fact]
    public void Cosine_HandlesZeroVectors_AndRejectsDifferentSizes()
    {
        Assert.Equal(0, VectorMath.Cosine([0f, 0f], [1f, 2f]));
        Assert.Equal(-1.0, VectorMath.Cosine([1f, 0f], [-2f, 0f]), precision: 6);
        Assert.Throws<ArgumentException>(() => VectorMath.Cosine([1f], [1f, 2f]));
    }

    [Fact]
    public async Task Store_KeepsTextMetadataAndVector()
    {
        var chunks = await RepositoryChunksAsync(_token);
        var store = new SqliteChunkStore(_database);
        float[] vector = [0.5f, -1.25f, 3f];

        await store.UpsertAsync([new StoredChunk(chunks[0], vector, "teste")], _token);
        var stored = Assert.Single(await store.GetAllAsync(_token));

        Assert.Equal(chunks[0].Id, stored.Chunk.Id);
        Assert.Equal(chunks[0].Citation, stored.Chunk.Citation);
        Assert.Equal(chunks[0].Access, stored.Chunk.Access);
        Assert.Equal(chunks[0].Hash, stored.Chunk.Hash);
        Assert.Equal(vector, stored.Vector.ToArray());
    }

    [Fact]
    public async Task Indexer_OnlyEmbedsWhatChanged()
    {
        var chunks = await RepositoryChunksAsync(_token);
        var store = new SqliteChunkStore(_database);
        using var generator = new CountingGenerator();
        var indexer = new KnowledgeIndexer(generator, store, "hashing-256");

        var first = await indexer.IndexAsync(chunks, _token);
        var second = await indexer.IndexAsync(chunks, _token);

        var edited = chunks.ToList();
        edited[0] = edited[0] with { Text = edited[0].Text + " Texto novo.", Hash = "outro-hash" };
        edited.RemoveAt(edited.Count - 1);
        var third = await indexer.IndexAsync(edited, _token);

        Assert.Equal((chunks.Count, 0, 0), (first.Embedded, first.Unchanged, first.Removed));
        Assert.Equal((0, chunks.Count, 0), (second.Embedded, second.Unchanged, second.Removed));
        Assert.Equal((1, chunks.Count - 2, 1), (third.Embedded, third.Unchanged, third.Removed));
        Assert.Equal(chunks.Count + 1, generator.Texts);
        Assert.Equal(chunks.Count - 1, (await store.GetAllAsync(_token)).Count);
    }

    [Fact]
    public async Task Indexer_ChangingTheModel_EmbedsEverythingAgain()
    {
        var chunks = await RepositoryChunksAsync(_token);
        var store = new SqliteChunkStore(_database);
        using var generator = new HashingEmbeddingGenerator();
        await new KnowledgeIndexer(generator, store, "modelo-a").IndexAsync(chunks, _token);

        var report = await new KnowledgeIndexer(generator, store, "modelo-b").IndexAsync(chunks, _token);

        Assert.Equal(chunks.Count, report.Embedded);
    }

    [Theory]
    [InlineData("como pedir férias e em quantos períodos dividir", "politica-de-ferias")]
    [InlineData("minha conta foi bloqueada depois de errar a senha", "senhas-e-acessos")]
    [InlineData("posso pedir adiantamento para uma viagem longa", "reembolso-de-despesas")]
    public async Task Search_FindsTheRightDocumentFirst(string question, string expectedDocument)
    {
        var store = new SqliteChunkStore(_database);
        using var generator = new HashingEmbeddingGenerator();
        await new KnowledgeIndexer(generator, store, HashingEmbeddingGenerator.ModelName).IndexAsync(await RepositoryChunksAsync(_token), _token);

        var hits = await new SemanticSearch(generator, store, HashingEmbeddingGenerator.ModelName).SearchAsync(question, 3, _token);

        Assert.Equal(expectedDocument, hits[0].Chunk.DocumentId);
        Assert.True(hits[0].Score >= hits[1].Score);
    }

    [Fact]
    public async Task Search_WithAnotherModelInTheIndex_ExplainsWhatToDo()
    {
        var store = new SqliteChunkStore(_database);
        using var generator = new HashingEmbeddingGenerator();
        await new KnowledgeIndexer(generator, store, "modelo-a").IndexAsync(await RepositoryChunksAsync(_token), _token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SemanticSearch(generator, store, "modelo-b").SearchAsync("férias", cancellationToken: _token));

        Assert.Contains("mimir ingerir", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_KnowsTheProviders_AndAsksForWhatIsMissing()
    {
        var (fake, model) = EmbeddingGeneratorFactory.Create(new EmbeddingOptions { Provider = LlmProvider.Fake }, _ => null);
        using (fake)
        {
            Assert.Equal(HashingEmbeddingGenerator.ModelName, model);
        }

        var (ollama, ollamaModel) = EmbeddingGeneratorFactory.Create(
            new EmbeddingOptions { Provider = LlmProvider.Ollama, Model = "paraphrase-multilingual", Endpoint = "http://localhost:11434" }, _ => null);
        using (ollama)
        {
            Assert.Equal("ollama/paraphrase-multilingual", ollamaModel);
        }

        Assert.Throws<InvalidOperationException>(() => EmbeddingGeneratorFactory.Create(
            new EmbeddingOptions { Provider = LlmProvider.OpenAI, Model = "text-embedding-3-small" }, _ => null));
        Assert.Throws<InvalidOperationException>(() => EmbeddingGeneratorFactory.Create(
            new EmbeddingOptions { Provider = LlmProvider.Anthropic, Model = "x" }, _ => null));
    }
}
