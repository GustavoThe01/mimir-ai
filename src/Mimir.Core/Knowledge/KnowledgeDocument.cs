using System.Globalization;
using Mimir.Core.Llm;

namespace Mimir.Core.Knowledge;

/// <summary>Um documento da base de conhecimento, já com os metadados lidos do cabeçalho.</summary>
/// <param name="Id">Nome do arquivo sem extensão. É o que aparece na citação.</param>
/// <param name="Access">Perfis que podem ler o documento. <c>todos</c> libera para qualquer pessoa.</param>
/// <param name="Lines">Todas as linhas do arquivo, para que os trechos guardem a linha original.</param>
/// <param name="BodyStartLine">Primeira linha (base 1) depois do cabeçalho.</param>
public sealed record KnowledgeDocument(
    string Id,
    string Title,
    Department Department,
    IReadOnlyList<string> Access,
    DateOnly UpdatedAt,
    IReadOnlyList<string> Lines,
    int BodyStartLine);

/// <summary>
/// Lê o documento em Markdown com um cabeçalho simples entre linhas <c>---</c>:
/// <c>titulo</c>, <c>setor</c>, <c>acesso</c> (lista entre colchetes) e <c>atualizado_em</c> (AAAA-MM-DD).
/// </summary>
/// <remarks>
/// O cabeçalho é obrigatório e validado na ingestão: um documento sem perfil de acesso não pode entrar na base,
/// porque o filtro de permissão do Dia 10 depende dele. Sem acesso declarado, o documento seria público por engano.
/// </remarks>
public static class DocumentParser
{
    public static readonly IReadOnlySet<string> KnownProfiles =
        new HashSet<string>(["todos", "rh", "financeiro", "juridico", "ti", "diretoria"], StringComparer.Ordinal);

    public static KnowledgeDocument Parse(string id, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(content);

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw Invalid(id, "o arquivo precisa começar com o cabeçalho entre linhas ---");
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0)
        {
            throw Invalid(id, "o cabeçalho não foi fechado com ---");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < end; i++)
        {
            var separator = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                fields[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
            }
        }

        return new KnowledgeDocument(
            id,
            Required(fields, "titulo", id),
            ParseDepartment(Required(fields, "setor", id), id),
            ParseAccess(Required(fields, "acesso", id), id),
            ParseDate(Required(fields, "atualizado_em", id), id),
            lines,
            end + 2);
    }

    private static string Required(Dictionary<string, string> fields, string name, string id) =>
        fields.TryGetValue(name, out var value) && value.Length > 0 ? value : throw Invalid(id, $"falta o campo '{name}'");

    private static Department ParseDepartment(string value, string id) =>
        Enum.TryParse<Department>(value, ignoreCase: true, out var department)
        && Enum.IsDefined(department)
        && !int.TryParse(value, out _)
            ? department
            : throw Invalid(id, $"setor desconhecido: '{value}'");

    private static List<string> ParseAccess(string value, string id)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']'))
        {
            throw Invalid(id, "acesso deve ser uma lista, como [todos] ou [rh, diretoria]");
        }

        var profiles = value[1..^1]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(profile => profile.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = profiles.Where(profile => !KnownProfiles.Contains(profile)).ToList();
        if (profiles.Count == 0 || unknown.Count > 0)
        {
            throw Invalid(id, $"perfil de acesso desconhecido ou vazio: [{string.Join(", ", unknown)}]");
        }

        return profiles;
    }

    private static DateOnly ParseDate(string value, string id) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw Invalid(id, $"data inválida em atualizado_em: '{value}'");

    private static FormatException Invalid(string id, string reason) => new($"Documento '{id}': {reason}.");
}
