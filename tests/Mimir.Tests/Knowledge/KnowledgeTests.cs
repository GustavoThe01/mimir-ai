using Mimir.Core.Knowledge;
using Mimir.Core.Llm;
using Mimir.Infrastructure.Knowledge;

namespace Mimir.Tests.Knowledge;

public class KnowledgeTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    private const string Sample = """
        ---
        titulo: Política de teste
        setor: TI
        acesso: [rh, Diretoria]
        atualizado_em: 2026-11-09
        ---

        # Política de teste

        Introdução da política.

        ## Primeira seção

        Primeiro parágrafo da primeira seção.

        Segundo parágrafo da primeira seção.

        ## Segunda seção

        ### Detalhe

        Texto do detalhe.
        """;

    private static KnowledgeDocument Doc(string content = Sample) => DocumentParser.Parse("politica-de-teste", content);

    [Fact]
    public void Parser_ReadsTheHeader()
    {
        var document = Doc();

        Assert.Equal("Política de teste", document.Title);
        Assert.Equal(Department.Ti, document.Department);
        Assert.Equal(["rh", "diretoria"], document.Access);
        Assert.Equal(new DateOnly(2026, 11, 9), document.UpdatedAt);
        Assert.Equal(7, document.BodyStartLine);
    }

    [Theory]
    [InlineData("# Sem cabeçalho", "começar com o cabeçalho")]
    [InlineData("---\ntitulo: X\n", "não foi fechado")]
    [InlineData("---\nsetor: TI\nacesso: [todos]\natualizado_em: 2026-01-01\n---\n", "'titulo'")]
    [InlineData("---\ntitulo: X\nsetor: MARKETING\nacesso: [todos]\natualizado_em: 2026-01-01\n---\n", "setor desconhecido")]
    [InlineData("---\ntitulo: X\nsetor: TI\nacesso: todos\natualizado_em: 2026-01-01\n---\n", "deve ser uma lista")]
    [InlineData("---\ntitulo: X\nsetor: TI\nacesso: [estagiarios]\natualizado_em: 2026-01-01\n---\n", "perfil de acesso desconhecido")]
    [InlineData("---\ntitulo: X\nsetor: TI\nacesso: []\natualizado_em: 2026-01-01\n---\n", "vazio")]
    [InlineData("---\ntitulo: X\nsetor: TI\nacesso: [todos]\natualizado_em: 09/11/2026\n---\n", "data inválida")]
    public void Parser_RejectsInvalidHeaders(string content, string message)
    {
        var exception = Assert.Throws<FormatException>(() => Doc(content));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunker_FollowsTheSections_AndKeepsTheOriginalLines()
    {
        var chunks = Chunker.Split(Doc());

        Assert.Equal(
            ["Política de teste", "Política de teste › Primeira seção", "Política de teste › Segunda seção › Detalhe"],
            chunks.Select(c => c.Section));
        Assert.Equal("Primeiro parágrafo da primeira seção.\n\nSegundo parágrafo da primeira seção.", chunks[1].Text);
        Assert.Equal("politica-de-teste.md#L14-L16", chunks[1].Citation);
        Assert.Equal(["politica-de-teste#1", "politica-de-teste#2", "politica-de-teste#3"], chunks.Select(c => c.Id));
        Assert.All(chunks, c => Assert.Equal(["rh", "diretoria"], c.Access));
    }

    [Fact]
    public void Chunker_PacksParagraphsUpToTheLimit_AndOverlapsTheLastOne()
    {
        var paragraphs = Enumerable.Range(1, 6).Select(i => $"Parágrafo {i} " + new string('x', 40)).ToList();
        var content = "---\ntitulo: T\nsetor: RH\nacesso: [todos]\natualizado_em: 2026-01-01\n---\n\n## Seção\n\n"
            + string.Join("\n\n", paragraphs);

        var chunks = Chunker.Split(Doc(content), new ChunkingOptions(MaxCharacters: 160, OverlapCharacters: 60));

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 160, $"{c.Text.Length} caracteres"));
        Assert.True(chunks.Count >= 3);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousLast = chunks[i - 1].Text.Split("\n\n")[^1];
            Assert.StartsWith(previousLast, chunks[i].Text, StringComparison.Ordinal);
        }

        Assert.All(paragraphs, p => Assert.Contains(chunks, c => c.Text.Contains(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void Chunker_SplitsALongParagraphIntoSentences_NeverPassingTheLimit()
    {
        var sentences = string.Join(' ', Enumerable.Range(1, 30).Select(i => $"Frase número {i} do parágrafo longo."));
        var content = "---\ntitulo: T\nsetor: RH\nacesso: [todos]\natualizado_em: 2026-01-01\n---\n\n## Seção\n\n" + sentences;

        var chunks = Chunker.Split(Doc(content), new ChunkingOptions(MaxCharacters: 200, OverlapCharacters: 50));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200));
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Chunker_SameText_SameHash()
    {
        var first = Chunker.Split(Doc());
        var second = Chunker.Split(Doc());

        Assert.Equal(first.Select(c => c.Hash), second.Select(c => c.Hash));
        Assert.Equal(64, first[0].Hash.Length);
        Assert.Equal("Política de teste › Primeira seção\n" + first[1].Text, first[1].ContextualText);
    }

    [Fact]
    public void Chunker_RejectsOptionsThatMakeNoSense()
    {
        Assert.Throws<ArgumentException>(() => Chunker.Split(Doc(), new ChunkingOptions(MaxCharacters: 50)));
        Assert.Throws<ArgumentException>(() => Chunker.Split(Doc(), new ChunkingOptions(MaxCharacters: 200, OverlapCharacters: 150)));
    }

    [Fact]
    public async Task RepositoryKnowledgeBase_IsValid_AndHasRestrictedDocuments()
    {
        var documents = await FileDocumentSource.LoadAsync(Path.Combine(AppContext.BaseDirectory, "knowledge"), _token);
        var chunks = documents.SelectMany(d => Chunker.Split(d)).ToList();

        Assert.Equal(8, documents.Count);
        Assert.All(chunks, c => Assert.InRange(c.Text.Length, 1, ChunkingOptions.Default.MaxCharacters));
        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        Assert.DoesNotContain(documents.Single(d => d.Id == "faixas-salariais").Access, a => a == "todos");
        Assert.Contains(chunks, c => c.DocumentId == "glossario-financeiro" && c.Text.Contains("livro-razão", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingFolder_IsAClearError()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => FileDocumentSource.LoadAsync("pasta-que-nao-existe", _token));
    }
}
