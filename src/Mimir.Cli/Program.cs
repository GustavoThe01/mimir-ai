using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mimir.Core.Evaluation;
using Mimir.Core.Llm;
using Mimir.Infrastructure.Llm;

// Sem isso, os acentos da resposta saem errados no terminal do Windows.
Console.OutputEncoding = Encoding.UTF8;

const string ClassifyCommand = "classificar";
const string EvaluateCommand = "avaliar";
const string DefaultDataset = "datasets/classificacao-chamados.v1.jsonl";

var command = args.Length > 0 && args[0] is ClassifyCommand or EvaluateCommand ? args[0] : null;
var text = string.Join(' ', command is null ? args : args[1..]);

if (command != EvaluateCommand && string.IsNullOrWhiteSpace(text))
{
    Console.WriteLine("Uso:");
    Console.WriteLine("  mimir \"sua pergunta\"");
    Console.WriteLine($"  mimir {ClassifyCommand} \"texto do chamado\"");
    Console.WriteLine($"  mimir {EvaluateCommand} [arquivo.jsonl]   (padrão: {DefaultDataset})");
    return 1;
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();

try
{
    using IChatClient chatClient = ChatClientFactory.Create(options, Environment.GetEnvironmentVariable);

    switch (command)
    {
        case ClassifyCommand:
            var classification = await new TicketClassifier(chatClient).ClassifyAsync(text);
            Console.WriteLine($"Setor: {classification.Department.ToString().ToUpperInvariant()}");
            Console.WriteLine($"Motivo: {classification.Reason}");
            return 0;

        case EvaluateCommand:
            return await EvaluateAsync(chatClient, options, string.IsNullOrWhiteSpace(text) ? null : text.Trim());

        default:
            await foreach (var part in new KnowledgeAssistant(chatClient).AskStreamingAsync(text))
            {
                Console.Write(part);
            }

            Console.WriteLine();
            return 0;
    }
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Não consegui falar com o provedor {options.Provider}: {exception.Message}");
    return 3;
}
catch (StructuredOutputException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 4;
}
catch (Exception exception) when (exception is FormatException or IOException)
{
    Console.Error.WriteLine(exception.Message);
    return 5;
}

static async Task<int> EvaluateAsync(IChatClient chatClient, LlmOptions options, string? datasetPath)
{
    var path = datasetPath ?? Path.Combine(AppContext.BaseDirectory, DefaultDataset);
    var cases = EvaluationDataset.Parse(await File.ReadAllTextAsync(path));
    var model = $"{options.Provider} · {options.Model ?? "modelo padrão"}";

    Console.WriteLine($"Avaliando {cases.Count} casos com {model}...");
    var done = 0;
    var progress = new SynchronousProgress(result =>
    {
        done++;
        var mark = result.IsCorrect ? "ok  " : "ERRO";
        var predicted = result.Predicted?.ToString().ToUpperInvariant() ?? "inválida";
        Console.WriteLine($"  [{done,2}/{cases.Count}] {mark} {result.Case.Id,-8} → {predicted} ({result.Elapsed.TotalSeconds:0.0} s)");
    });

    var report = await new ClassificationEvaluator(new TicketClassifier(chatClient)).RunAsync(cases, model, progress);

    Directory.CreateDirectory("relatorios");
    var reportPath = Path.Combine(
        "relatorios",
        $"avaliacao-{report.StartedAt.ToUniversalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.md");
    await File.WriteAllTextAsync(reportPath, report.ToMarkdown());

    Console.WriteLine();
    Console.WriteLine($"Acertos: {report.Correct} de {report.Total} ({report.Accuracy:P1})");
    Console.WriteLine($"Relatório: {Path.GetFullPath(reportPath)}");
    return 0;
}

/// <summary>Mostra o progresso na hora, na mesma thread, sem o contexto de sincronização do <see cref="Progress{T}"/>.</summary>
internal sealed class SynchronousProgress(Action<CaseResult> report) : IProgress<CaseResult>
{
    public void Report(CaseResult value) => report(value);
}
