using Mimir.Core.Llm;

namespace Mimir.Core.Evaluation;

/// <summary>
/// Roda o classificador em cada caso do conjunto e monta o relatório.
/// </summary>
/// <remarks>
/// Os casos rodam um de cada vez: o modelo local atende uma requisição por vez, e assim a latência medida
/// é a de cada chamada, sem fila. Uma resposta inválida conta como erro e não interrompe a rodada.
/// </remarks>
public sealed class ClassificationEvaluator(TicketClassifier classifier, TimeProvider timeProvider)
{
    public ClassificationEvaluator(TicketClassifier classifier)
        : this(classifier, TimeProvider.System)
    {
    }

    public async Task<EvaluationReport> RunAsync(
        IReadOnlyList<EvaluationCase> cases,
        string model,
        IProgress<CaseResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var startedAt = timeProvider.GetUtcNow();
        var results = new List<CaseResult>(cases.Count);

        foreach (var item in cases)
        {
            var start = timeProvider.GetTimestamp();
            CaseResult result;
            try
            {
                var classification = await classifier.ClassifyAsync(item.Ticket, cancellationToken);
                result = new CaseResult(item, classification.Department, null, timeProvider.GetElapsedTime(start));
            }
            catch (StructuredOutputException exception)
            {
                result = new CaseResult(item, null, exception.Message, timeProvider.GetElapsedTime(start));
            }

            results.Add(result);
            progress?.Report(result);
        }

        return new EvaluationReport(model, PromptLibrary.TicketClassification, startedAt, results);
    }
}
