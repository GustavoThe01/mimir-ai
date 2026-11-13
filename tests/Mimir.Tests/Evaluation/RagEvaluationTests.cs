using Mimir.Core.Evaluation;
using Mimir.Core.Llm;

namespace Mimir.Tests.Evaluation;

public class RagEvaluationTests
{
    private static GroundedAnswer Answer(string text, params string[] citations) =>
        new(true, text, [.. citations.Select((c, i) => new CitedSource(i + 1, c, "T", "S"))], null);

    private static readonly GroundedAnswer DontKnow = GroundedAnswer.NotFound(NoAnswerReason.SourcesDoNotAnswer);

    private static RagCase Case(string? document, string? fact = null) => new("c-1", "pergunta", [], document, fact);

    private static bool Everything(string document) => true;

    [Fact]
    public void RightSourceAndFact_IsCorrect_IgnoringCaseAndSpaces()
    {
        var outcome = RagEvaluation.Classify(Case("politica-de-ferias", "14 dias"),
            Answer("Um período com pelo menos 14  DIAS [1].", "politica-de-ferias.md#L24-L24"), Everything);

        Assert.Equal(RagOutcome.Correct, outcome);
    }

    [Fact]
    public void DontKnowWhenExpected_IsCorrect()
    {
        Assert.Equal(RagOutcome.CorrectDontKnow, RagEvaluation.Classify(Case(null), DontKnow, Everything));
    }

    [Theory]
    [InlineData(null, "trabalho-remoto.md#L16-L16", RagOutcome.ShouldNotAnswer)]
    [InlineData("politica-de-ferias", "trabalho-remoto.md#L16-L16", RagOutcome.WrongSource)]
    [InlineData("politica-de-ferias", "politica-de-ferias.md#L24-L24", RagOutcome.MissingFact)]
    public void WrongAnswers_AreSeparatedByKind(string? expected, string citation, RagOutcome outcome)
    {
        Assert.Equal(outcome, RagEvaluation.Classify(Case(expected, "14 dias"), Answer("Não lembro [1].", citation), Everything));
    }

    [Fact]
    public void DontKnow_WhenTheAnswerWasReadable_IsAFalseDontKnow()
    {
        Assert.Equal(RagOutcome.FalseDontKnow, RagEvaluation.Classify(Case("senhas-e-acessos"), DontKnow, Everything));
    }

    [Fact]
    public void CitingADocumentTheProfileCannotRead_IsALeak_EvenIfItLooksCorrect()
    {
        var outcome = RagEvaluation.Classify(Case("faixas-salariais", "10.500"),
            Answer("Até R$ 10.500 [1].", "faixas-salariais.md#L14-L14"), document => document != "faixas-salariais");

        Assert.Equal(RagOutcome.Leak, outcome);
    }

    [Fact]
    public void Parse_ReadsProfilesAndOptionalFields()
    {
        var cases = RagEvaluation.Parse("""
            {"id": "a", "pergunta": "p1", "perfis": ["rh"], "documento": "faixas-salariais", "fato": "10.500"}
            {"id": "b", "pergunta": "p2", "perfis": [], "documento": null, "fato": null}
            """);

        Assert.Equal(["rh"], cases[0].Profiles);
        Assert.Null(cases[1].ExpectedDocument);
        Assert.Throws<FormatException>(() => RagEvaluation.Parse("""{"id": "a", "pergunta": "p", "documento": null}"""));
    }

    [Fact]
    public void RealDataset_IsValid_AndCoversRestrictedQuestions()
    {
        var cases = RagEvaluation.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "datasets", "rag.v1.jsonl")));

        Assert.Equal(16, cases.Count);
        Assert.Contains(cases, c => c.Profiles.Count == 0 && c.ExpectedDocument is null && c.Question.Contains("salarial", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Profiles.Contains("rh") && c.ExpectedDocument == "faixas-salariais");
    }

    [Fact]
    public void DocumentOf_TakesTheFileName()
    {
        Assert.Equal("politica-de-ferias", RagEvaluation.DocumentOf("politica-de-ferias.md#L24-L24"));
    }
}
