using System.Security.Cryptography;
using System.Text;
using Mimir.Core.Llm;

namespace Mimir.Core.Knowledge;

/// <summary>Um trecho do documento: a unidade que será indexada, buscada e citada.</summary>
/// <param name="Section">Caminho da seção, como "Política de férias › Como pedir".</param>
/// <param name="StartLine">Primeira linha do trecho no arquivo original (base 1).</param>
/// <param name="Hash">SHA-256 do texto. Na próxima ingestão, trecho com o mesmo hash não precisa de novo embedding.</param>
public sealed record KnowledgeChunk(
    string Id,
    string DocumentId,
    string Title,
    string Section,
    string Text,
    int StartLine,
    int EndLine,
    Department Department,
    IReadOnlyList<string> Access,
    string Hash)
{
    /// <summary>Onde o trecho está. É o que a resposta vai citar no Dia 09.</summary>
    public string Citation => $"{DocumentId}.md#L{StartLine}-L{EndLine}";

    /// <summary>
    /// Texto com o título e a seção na frente. Um trecho como "O prazo é de 5 dias úteis" não diz de quê;
    /// com o contexto, a busca sabe que é o prazo de revisão de contratos.
    /// </summary>
    public string ContextualText => $"{Section}\n{Text}";
}

/// <summary>Tamanho máximo do trecho e quanto do fim de um trecho se repete no começo do próximo.</summary>
public sealed record ChunkingOptions(int MaxCharacters = 700, int OverlapCharacters = 160)
{
    public static readonly ChunkingOptions Default = new();
}

/// <summary>
/// Divide o documento respeitando a estrutura: um trecho nunca atravessa uma seção (título <c>##</c>),
/// e dentro da seção os parágrafos são agrupados até o limite de tamanho.
/// </summary>
/// <remarks>
/// <para>
/// Parágrafo maior que o limite é quebrado em frases. A última frase de um trecho se repete no começo do próximo
/// (sobreposição), para que uma informação na fronteira entre dois trechos não fique sem contexto nos dois.
/// </para>
/// <para>
/// Cortar por número fixo de caracteres seria mais simples, mas partiria frases ao meio e misturaria seções:
/// um trecho com o fim das regras de senha e o começo das regras de bloqueio atrapalha a busca e a citação.
/// </para>
/// </remarks>
public static class Chunker
{
    private const string SectionSeparator = " › ";

    public static IReadOnlyList<KnowledgeChunk> Split(KnowledgeDocument document, ChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= ChunkingOptions.Default;
        if (options.MaxCharacters < 100 || options.OverlapCharacters < 0 || options.OverlapCharacters >= options.MaxCharacters / 2)
        {
            throw new ArgumentException("Use MaxCharacters >= 100 e OverlapCharacters menor que metade dele.", nameof(options));
        }

        var chunks = new List<KnowledgeChunk>();
        foreach (var section in Sections(document))
        {
            foreach (var group in Pack(section.Units, options))
            {
                var text = string.Join("\n\n", group.Select(unit => unit.Text));
                chunks.Add(new KnowledgeChunk(
                    $"{document.Id}#{chunks.Count + 1}",
                    document.Id,
                    document.Title,
                    section.Path,
                    text,
                    group[0].StartLine,
                    group[^1].EndLine,
                    document.Department,
                    document.Access,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))));
            }
        }

        return chunks;
    }

    private sealed record Unit(string Text, int StartLine, int EndLine);

    private sealed record Section(string Path, List<Unit> Units);

    /// <summary>Percorre o corpo e devolve cada seção com os seus parágrafos (ou frases, se o parágrafo for longo).</summary>
    private static List<Section> Sections(KnowledgeDocument document)
    {
        var sections = new List<Section>();
        string? topic = null;
        var current = new Section(document.Title, []);
        var paragraph = new List<(string Text, int Line)>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                current.Units.Add(new Unit(string.Join(' ', paragraph.Select(p => p.Text)), paragraph[0].Line, paragraph[^1].Line));
                paragraph.Clear();
            }
        }

        for (var index = document.BodyStartLine - 1; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index].Trim();
            var lineNumber = index + 1;

            if (line.StartsWith('#'))
            {
                FlushParagraph();
                var level = line.TakeWhile(c => c == '#').Count();
                var heading = line[level..].Trim();
                if (level == 1)
                {
                    continue;
                }

                if (current.Units.Count > 0)
                {
                    sections.Add(current);
                }

                // ## abre um tópico; ### fica dentro do tópico atual.
                topic = level == 2 ? heading : topic;
                var path = level == 2 || topic is null
                    ? $"{document.Title}{SectionSeparator}{heading}"
                    : $"{document.Title}{SectionSeparator}{topic}{SectionSeparator}{heading}";
                current = new Section(path, []);
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            paragraph.Add((line, lineNumber));
        }

        FlushParagraph();
        if (current.Units.Count > 0)
        {
            sections.Add(current);
        }

        return sections;
    }

    /// <summary>Agrupa as unidades até o limite, quebrando em frases as que sozinhas passam dele.</summary>
    private static List<List<Unit>> Pack(List<Unit> units, ChunkingOptions options)
    {
        var pieces = units.SelectMany(unit => unit.Text.Length <= options.MaxCharacters ? new[] { unit } : Sentences(unit, options)).ToList();
        var groups = new List<List<Unit>>();
        var group = new List<Unit>();
        var size = 0;

        foreach (var piece in pieces)
        {
            if (group.Count > 0 && size + piece.Text.Length + 2 > options.MaxCharacters)
            {
                groups.Add(group);
                var last = group[^1];
                group = last.Text.Length <= options.OverlapCharacters ? [last] : [];
                size = group.Sum(unit => unit.Text.Length + 2);
            }

            group.Add(piece);
            size += piece.Text.Length + 2;
        }

        if (group.Count > 0)
        {
            groups.Add(group);
        }

        return groups;
    }

    private static IEnumerable<Unit> Sentences(Unit unit, ChunkingOptions options)
    {
        var buffer = new StringBuilder();
        foreach (var sentence in SplitSentences(unit.Text))
        {
            if (buffer.Length > 0 && buffer.Length + sentence.Length + 1 > options.MaxCharacters)
            {
                yield return unit with { Text = buffer.ToString() };
                buffer.Clear();
            }

            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            // Uma frase sozinha maior que o limite é cortada em pedaços: é raro, mas o limite precisa valer sempre.
            var rest = sentence;
            while (rest.Length > options.MaxCharacters)
            {
                yield return unit with { Text = rest[..options.MaxCharacters] };
                rest = rest[options.MaxCharacters..];
            }

            buffer.Append(rest);
        }

        if (buffer.Length > 0)
        {
            yield return unit with { Text = buffer.ToString() };
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ')
            {
                yield return text[start..(i + 1)];
                start = i + 2;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
