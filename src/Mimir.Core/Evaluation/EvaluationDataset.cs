using System.Text.Json;
using Mimir.Core.Llm;

namespace Mimir.Core.Evaluation;

/// <summary>Um chamado do conjunto de avaliação e o setor que eu considero correto para ele.</summary>
public sealed record EvaluationCase(string Id, string Ticket, Department Expected);

/// <summary>
/// Lê o conjunto de avaliação em JSON Lines: um caso por linha, com <c>id</c>, <c>ticket</c> e <c>expected</c>.
/// </summary>
/// <remarks>
/// JSON Lines porque cada caso é independente: dá para revisar a diferença de um caso no Git
/// e acrescentar casos sem mexer no resto do arquivo.
/// </remarks>
public static class EvaluationDataset
{
    public static IReadOnlyList<EvaluationCase> Parse(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);

        var cases = new List<EvaluationCase>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lines = jsonLines.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lineNumber = index + 1;
            var item = ParseLine(line, lineNumber);
            if (!ids.Add(item.Id))
            {
                throw new FormatException($"Linha {lineNumber}: o id '{item.Id}' está repetido.");
            }

            cases.Add(item);
        }

        if (cases.Count == 0)
        {
            throw new FormatException("O conjunto de avaliação está vazio.");
        }

        return cases;
    }

    private static EvaluationCase ParseLine(string line, int lineNumber)
    {
        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Linha {lineNumber}: não é um JSON válido.", exception);
        }

        var id = RequiredText(json, "id", lineNumber);
        var ticket = RequiredText(json, "ticket", lineNumber);
        var expected = RequiredText(json, "expected", lineNumber);

        if (!Enum.TryParse<Department>(expected, ignoreCase: true, out var department)
            || !Enum.IsDefined(department)
            || int.TryParse(expected, out _))
        {
            throw new FormatException($"Linha {lineNumber}: o setor '{expected}' não existe.");
        }

        return new EvaluationCase(id, ticket, department);
    }

    private static string RequiredText(JsonElement json, string property, int lineNumber) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new FormatException($"Linha {lineNumber}: o campo '{property}' é obrigatório.");
}
