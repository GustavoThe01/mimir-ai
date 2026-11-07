using System.Globalization;
using System.Text;
using Mimir.Core.Llm;

namespace Mimir.Core.Evaluation;

/// <summary>Resultado de um caso. <see cref="Predicted"/> fica nulo quando o modelo não devolveu resposta válida.</summary>
public sealed record CaseResult(EvaluationCase Case, Department? Predicted, string? Error, TimeSpan Elapsed)
{
    public bool IsCorrect => Predicted == Case.Expected;
}

/// <summary>Precisão e revocação de um setor. Suporte é quantos casos esperavam esse setor.</summary>
public sealed record DepartmentMetrics(Department Department, int Support, double Precision, double Recall)
{
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

/// <summary>
/// Consolida uma rodada de avaliação: acurácia, métricas por setor, matriz de confusão e os casos que erraram.
/// </summary>
public sealed class EvaluationReport
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public EvaluationReport(string model, string prompt, DateTimeOffset startedAt, IReadOnlyList<CaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new ArgumentException("A avaliação precisa de pelo menos um caso.", nameof(results));
        }

        Model = model;
        Prompt = prompt;
        StartedAt = startedAt;
        Results = results;
        PerDepartment = Enum.GetValues<Department>().Select(Metrics).ToList();
    }

    public string Model { get; }

    public string Prompt { get; }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<CaseResult> Results { get; }

    public IReadOnlyList<DepartmentMetrics> PerDepartment { get; }

    public int Total => Results.Count;

    public int Correct => Results.Count(r => r.IsCorrect);

    /// <summary>Casos em que o modelo não devolveu um JSON válido nem na segunda tentativa.</summary>
    public int InvalidOutputs => Results.Count(r => r.Predicted is null);

    public double Accuracy => (double)Correct / Total;

    public TimeSpan AverageLatency => TimeSpan.FromTicks((long)Results.Average(r => r.Elapsed.Ticks));

    /// <summary>Quantos casos esperavam <paramref name="expected"/> e receberam <paramref name="predicted"/>.</summary>
    public int Count(Department expected, Department? predicted) =>
        Results.Count(r => r.Case.Expected == expected && r.Predicted == predicted);

    public string ToMarkdown()
    {
        var departments = Enum.GetValues<Department>();
        var md = new StringBuilder();

        md.AppendLine("# Avaliação — classificação de chamados").AppendLine();
        md.AppendLine("| | |").AppendLine("|---|---|");
        md.AppendLine(Inv($"| Modelo | `{Model}` |"));
        md.AppendLine(Inv($"| Prompt | `{Prompt}` |"));
        md.AppendLine(Inv($"| Início | {StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC |"));
        md.AppendLine(Inv($"| Casos | {Total} |"));
        md.AppendLine(Inv($"| Acertos | **{Correct} de {Total} ({Percent(Accuracy)})** |"));
        md.AppendLine(Inv($"| Respostas inválidas | {InvalidOutputs} |"));
        md.AppendLine(Inv($"| Latência média | {AverageLatency.TotalSeconds.ToString("0.0", PtBr)} s |"));
        md.AppendLine();

        md.AppendLine("## Por setor").AppendLine();
        md.AppendLine("| Setor | Casos | Precisão | Revocação | F1 |").AppendLine("|---|---:|---:|---:|---:|");
        foreach (var metric in PerDepartment)
        {
            md.AppendLine(Inv($"| {Name(metric.Department)} | {metric.Support} | {Percent(metric.Precision)} | {Percent(metric.Recall)} | {Percent(metric.F1)} |"));
        }

        md.AppendLine();
        md.AppendLine("## Matriz de confusão").AppendLine();
        md.AppendLine("Linhas: setor esperado. Colunas: setor que o modelo escolheu.").AppendLine();
        md.Append("| Esperado \\ Previsto |");
        foreach (var department in departments)
        {
            md.Append(Inv($" {Name(department)} |"));
        }

        md.AppendLine(" Inválida |");
        md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---:|", departments.Length + 1)));
        foreach (var expected in departments)
        {
            md.Append(Inv($"| {Name(expected)} |"));
            foreach (var predicted in departments)
            {
                md.Append(Inv($" {Count(expected, predicted)} |"));
            }

            md.AppendLine(Inv($" {Count(expected, null)} |"));
        }

        md.AppendLine();
        md.AppendLine("## Erros").AppendLine();
        var errors = Results.Where(r => !r.IsCorrect).ToList();
        if (errors.Count == 0)
        {
            md.AppendLine("Nenhum.");
            return md.ToString();
        }

        md.AppendLine("| Caso | Esperado | Previsto | Chamado |").AppendLine("|---|---|---|---|");
        foreach (var error in errors)
        {
            var predicted = error.Predicted is { } department ? Name(department) : "inválida";
            md.AppendLine(Inv($"| `{error.Case.Id}` | {Name(error.Case.Expected)} | {predicted} | {Escape(error.Case.Ticket)} |"));
        }

        return md.ToString();
    }

    private DepartmentMetrics Metrics(Department department)
    {
        var truePositives = Results.Count(r => r.Case.Expected == department && r.Predicted == department);
        var predicted = Results.Count(r => r.Predicted == department);
        var support = Results.Count(r => r.Case.Expected == department);

        return new DepartmentMetrics(
            department,
            support,
            predicted == 0 ? 0 : (double)truePositives / predicted,
            support == 0 ? 0 : (double)truePositives / support);
    }

    private static string Name(Department department) => department.ToString().ToUpperInvariant();

    private static string Percent(double value) => (value * 100).ToString("0.0", PtBr) + "%";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
