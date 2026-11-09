using Mimir.Core.Knowledge;

namespace Mimir.Infrastructure.Knowledge;

/// <summary>Lê os documentos <c>*.md</c> de uma pasta, em ordem alfabética, e devolve cada um já validado.</summary>
public static class FileDocumentSource
{
    public static async Task<IReadOnlyList<KnowledgeDocument>> LoadAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"A pasta de documentos não existe: {Path.GetFullPath(folder)}.");
        }

        var documents = new List<KnowledgeDocument>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            documents.Add(DocumentParser.Parse(Path.GetFileNameWithoutExtension(file), content));
        }

        return documents.Count > 0
            ? documents
            : throw new FormatException($"Nenhum documento .md encontrado em {Path.GetFullPath(folder)}.");
    }
}
