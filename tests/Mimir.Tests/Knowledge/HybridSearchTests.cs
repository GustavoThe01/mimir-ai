using Microsoft.Data.Sqlite;
using Mimir.Core.Evaluation;
using Mimir.Core.Knowledge;
using Mimir.Infrastructure.Knowledge;

namespace Mimir.Tests.Knowledge;

public sealed class HybridSearchTests : IDisposable
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"mimir-hibrida-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_database);
    }

    private async Task<(HybridSearch Search, SqliteChunkStore Store)> IndexedAsync()
    {
        var documents = await FileDocumentSource.LoadAsync(Path.Combine(AppContext.BaseDirectory, "knowledge"), _token);
        var store = new SqliteChunkStore(_database);
        var generator = new HashingEmbeddingGenerator();
        await new KnowledgeIndexer(generator, store, HashingEmbeddingGenerator.ModelName)
            .IndexAsync([.. documents.SelectMany(d => Chunker.Split(d))], _token);
        return (new HybridSearch(generator, store, HashingEmbeddingGenerator.ModelName), store);
    }

    [Theory]
    [InlineData("o que é \"LGPD\"?", "\"que\" OR \"LGPD\"")]
    [InlineData("prazo: 48h (horas)", "\"prazo\" OR \"48h\" OR \"horas\"")]
    [InlineData("a e o ?!", "")]
    public void MatchExpression_QuotesEachTerm_SoThePunctuationCannotBreakTheQuery(string question, string expected)
    {
        Assert.Equal(expected, SqliteChunkStore.ToMatchExpression(question));
    }

    [Theory]
    [InlineData("LGPD", "contratos-com-fornecedores")]
    [InlineData("abono pecuniário", "politica-de-ferias")]
    [InlineData("livro diario", "glossario-financeiro")]
    public async Task TextSearch_FindsExactTerms_EvenWithoutAccents(string term, string document)
    {
        var (_, store) = await IndexedAsync();

        var ids = await store.SearchTextAsync(term, 5, _token);

        Assert.StartsWith(document + "#", ids[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextSearch_WithoutUsefulTerms_ReturnsNothing()
    {
        var (_, store) = await IndexedAsync();

        Assert.Empty(await store.SearchTextAsync("a o ?", 5, _token));
    }

    [Fact]
    public async Task Hybrid_ShowsWhereEachResultCameFrom_AndSumsTheRrf()
    {
        var (search, _) = await IndexedAsync();

        var hits = await search.SearchAsync("MFA aplicativo autenticador", SearchMode.Hybrid, 5, _token);

        var top = hits[0];
        Assert.Equal("senhas-e-acessos", top.Chunk.DocumentId);
        Assert.NotNull(top.TextRank);
        var expected = (top.VectorRank is { } v ? 1.0 / (HybridSearch.RrfK + v) : 0) + 1.0 / (HybridSearch.RrfK + top.TextRank!.Value);
        Assert.Equal(expected, top.Score, precision: 10);
        Assert.True(hits.Zip(hits.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score));
    }

    [Fact]
    public async Task Modes_UseOnlyTheirOwnRanking()
    {
        var (search, _) = await IndexedAsync();

        var vector = await search.SearchAsync("férias", SearchMode.Vector, 3, _token);
        var text = await search.SearchAsync("férias", SearchMode.Text, 3, _token);

        Assert.All(vector, hit => Assert.Null(hit.TextRank));
        Assert.All(text, hit => Assert.Null(hit.VectorRank));
    }

    [Fact]
    public async Task OldIndex_WithoutTextTable_IsRebuiltWhenOpened()
    {
        var (_, store) = await IndexedAsync();
        await using (var connection = new SqliteConnection($"Data Source={_database};Pooling=False"))
        {
            await connection.OpenAsync(_token);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chunk_fts";
            await command.ExecuteNonQueryAsync(_token);
        }

        Assert.NotEmpty(await store.SearchTextAsync("LGPD", 5, _token));
    }

    [Fact]
    public void Evaluation_CalculatesHitsAndMrr()
    {
        SearchCase Case(string id) => new(id, "pergunta", "doc", "tipo");

        var score = SearchEvaluation.Score(SearchMode.Hybrid, [(Case("a"), 1), (Case("b"), 2), (Case("c"), 4), (Case("d"), 0)]);

        Assert.Equal((4, 1, 2), (score.Cases, score.HitAt1, score.HitAt3));
        Assert.Equal((1 + 0.5 + 0.25 + 0) / 4, score.Mrr, precision: 10);
        Assert.Equal(["b", "c", "d"], score.Missed);
    }

    [Fact]
    public async Task Evaluation_RunsTheRepositoryDataset_InTheThreeModes()
    {
        var (search, _) = await IndexedAsync();
        var cases = SearchEvaluation.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "datasets", "busca.v1.jsonl"), _token));

        var scores = await SearchEvaluation.RunAsync(search, cases, _token);

        Assert.Equal(16, cases.Count);
        Assert.Equal([SearchMode.Vector, SearchMode.Text, SearchMode.Hybrid], scores.Select(s => s.Mode));
        Assert.All(scores, s => Assert.Equal(16, s.Cases));
    }

    [Fact]
    public void Evaluation_RejectsIncompleteCases()
    {
        Assert.Throws<FormatException>(() => SearchEvaluation.Parse("{\"id\": \"a\", \"pergunta\": \"x\"}"));
        Assert.Throws<FormatException>(() => SearchEvaluation.Parse(" "));
    }
}
