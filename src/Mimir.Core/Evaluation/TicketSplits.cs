using System.Text.Json;
using Mimir.Core.Llm;

namespace Mimir.Core.Evaluation;

/// <summary>Um chamado rotulado do conjunto de fine-tuning. O grupo é a situação de origem (as variações de uma mesma situação).</summary>
public sealed record LabeledTicket(string Id, string Group, string Text, Department Department);

/// <summary>Os três conjuntos do fine-tuning, gerados por <c>training/dados/gerar_chamados.py</c>.</summary>
public sealed record TicketSplits(IReadOnlyList<LabeledTicket> Train, IReadOnlyList<LabeledTicket> Validation, IReadOnlyList<LabeledTicket> Test)
{
    public static readonly string[] FileNames = ["treino.jsonl", "validacao.jsonl", "teste.jsonl"];

    public static TicketSplits Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var sets = FileNames.Select(name => Parse(File.ReadAllText(Path.Combine(folder, name)))).ToList();
        return new TicketSplits(sets[0], sets[1], sets[2]);
    }

    public static IReadOnlyList<LabeledTicket> Parse(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        return jsonLines.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select((line, index) =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(line);
                string Field(string name) => json.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                    ? text
                    : throw new FormatException($"Linha {index + 1}: o campo '{name}' é obrigatório.");
                var department = Field("setor");
                return Enum.TryParse<Department>(department, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                    ? new LabeledTicket(Field("id"), Field("grupo"), Field("texto"), parsed)
                    : throw new FormatException($"Linha {index + 1}: setor desconhecido '{department}'.");
            })
            .ToList();
    }

    /// <summary>Grupos que aparecem em mais de um conjunto. Precisa ser vazio, ou o teste mede memória.</summary>
    public IReadOnlyList<string> LeakingGroups()
    {
        var owners = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var sets = new[] { Train, Validation, Test };
        for (var i = 0; i < sets.Length; i++)
        {
            foreach (var ticket in sets[i])
            {
                if (!owners.TryGetValue(ticket.Group, out var where))
                {
                    owners[ticket.Group] = where = [];
                }

                where.Add(i);
            }
        }

        return [.. owners.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key).Order(StringComparer.Ordinal)];
    }
}
