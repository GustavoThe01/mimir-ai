using Microsoft.Data.Sqlite;
using Mimir.Core.Knowledge;
using Mimir.Infrastructure.Knowledge;

namespace Mimir.Tests.Knowledge;

/// <summary>Filtro por perfil na busca, com a base real e o índice no SQLite.</summary>
public sealed class AccessScopeTests : IDisposable
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"mimir-acesso-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_database);
    }

    private async Task<HybridSearch> IndexedAsync()
    {
        var documents = await FileDocumentSource.LoadAsync(Path.Combine(AppContext.BaseDirectory, "knowledge"), _token);
        var store = new SqliteChunkStore(_database);
        var generator = new HashingEmbeddingGenerator();
        await new KnowledgeIndexer(generator, store, HashingEmbeddingGenerator.ModelName)
            .IndexAsync([.. documents.SelectMany(d => Chunker.Split(d))], _token);
        return new HybridSearch(generator, store, HashingEmbeddingGenerator.ModelName);
    }

    private static KnowledgeChunk Chunk(params string[] access) =>
        new("x#1", "x", "X", "X", "texto", 1, 1, Core.Llm.Department.Outros, access, "hash");

    [Fact]
    public void Public_ReadsOnlyDocumentsForEveryone()
    {
        Assert.True(AccessScope.Public.CanRead(Chunk("todos")));
        Assert.False(AccessScope.Public.CanRead(Chunk("rh", "diretoria")));
    }

    [Fact]
    public void Profile_ReadsItsDocuments_PlusTheOnesForEveryone_ButNotOtherProfiles()
    {
        var rh = AccessScope.For("RH");

        Assert.True(rh.CanRead(Chunk("rh", "diretoria")));
        Assert.True(rh.CanRead(Chunk("todos")));
        Assert.False(rh.CanRead(Chunk("juridico", "diretoria")));
        Assert.Equal("rh, todos", rh.ToString());
    }

    [Fact]
    public void UnknownProfile_IsAnError_NotASilentDenial()
    {
        var error = Assert.Throws<ArgumentException>(() => AccessScope.For("financas"));

        Assert.Contains("financas", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SearchMode.Hybrid)]
    [InlineData(SearchMode.Vector)]
    [InlineData(SearchMode.Text)]
    public async Task RestrictedDocument_DoesNotComeBack_InAnyKindOfSearch(SearchMode mode)
    {
        var search = await IndexedAsync();

        var forEveryone = await search.SearchAsync("faixa salarial desenvolvimento pleno reajuste", mode, 20, AccessScope.Public, _token);
        var forRh = await search.SearchAsync("faixa salarial desenvolvimento pleno reajuste", mode, 20, AccessScope.For("rh"), _token);

        Assert.DoesNotContain(forEveryone, hit => hit.Chunk.DocumentId is "faixas-salariais" or "contratos-com-fornecedores");
        Assert.Contains(forRh, hit => hit.Chunk.DocumentId == "faixas-salariais");
        Assert.DoesNotContain(forRh, hit => hit.Chunk.DocumentId == "contratos-com-fornecedores");
    }

    [Fact]
    public async Task WithoutScope_TheDiagnosticSearchStillSeesEverything()
    {
        var search = await IndexedAsync();

        var hits = await search.SearchAsync("faixa salarial desenvolvimento pleno", SearchMode.Hybrid, 5, _token);

        Assert.Contains(hits, hit => hit.Chunk.DocumentId == "faixas-salariais");
    }
}
