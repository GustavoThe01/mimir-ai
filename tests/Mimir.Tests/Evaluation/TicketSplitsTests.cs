using Mimir.Core.Evaluation;
using Mimir.Core.Llm;

namespace Mimir.Tests.Evaluation;

/// <summary>
/// Confere o conjunto de fine-tuning que está no repositório. Se alguém regerar os arquivos com um defeito
/// (vazamento entre conjuntos, setor desbalanceado, texto repetido), o CI quebra.
/// </summary>
public class TicketSplitsTests
{
    private static readonly TicketSplits Splits = TicketSplits.Load(Path.Combine(AppContext.BaseDirectory, "datasets", "chamados"));

    [Fact]
    public void Sizes_Are280_60_60()
    {
        Assert.Equal(280, Splits.Train.Count);
        Assert.Equal(60, Splits.Validation.Count);
        Assert.Equal(60, Splits.Test.Count);
    }

    [Fact]
    public void EverySet_IsBalancedAcrossTheFiveDepartments()
    {
        foreach (var (set, perDepartment) in new[] { (Splits.Train, 56), (Splits.Validation, 12), (Splits.Test, 12) })
        {
            var counts = set.GroupBy(t => t.Department).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(Enum.GetValues<Department>().Length, counts.Count);
            Assert.All(counts.Values, count => Assert.Equal(perDepartment, count));
        }
    }

    [Fact]
    public void NoSituation_AppearsInTwoSets()
    {
        Assert.Empty(Splits.LeakingGroups());
    }

    [Fact]
    public void NoText_IsRepeated_AndIdsAreUnique()
    {
        var all = Splits.Train.Concat(Splits.Validation).Concat(Splits.Test).ToList();

        Assert.Equal(all.Count, all.Select(t => t.Text).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheDay04EvaluationSet_StaysOutOfTheTrainingData()
    {
        var day04 = EvaluationDataset.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "datasets", "classificacao-chamados.v1.jsonl")));
        var texts = Splits.Train.Concat(Splits.Validation).Concat(Splits.Test).Select(t => t.Text).ToList();

        Assert.DoesNotContain(day04, c => texts.Any(t => t.Contains(c.Ticket.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void LeakingGroups_FindsASituationSplitAcrossSets()
    {
        var ticket = new LabeledTicket("ti-01-1", "ti-01", "texto", Department.Ti);
        var leaking = new TicketSplits([ticket], [ticket with { Id = "ti-01-2" }], []);

        Assert.Equal(["ti-01"], leaking.LeakingGroups());
    }

    [Theory]
    [InlineData("""{"id": "x", "grupo": "g", "texto": "t", "setor": "MARKETING"}""")]
    [InlineData("""{"id": "x", "grupo": "g", "setor": "TI"}""")]
    public void Parse_RejectsUnknownDepartment_OrMissingField(string line)
    {
        Assert.Throws<FormatException>(() => TicketSplits.Parse(line));
    }
}
