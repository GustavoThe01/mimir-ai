using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

// Avisos (como uma nova tentativa) vão para o stderr, sem misturar com a resposta.
// Para ver tokens e tempo de cada chamada no log, use Logging__LogLevel__Default=Information.
using var loggerFactory = LoggerFactory.Create(logging => logging
    .SetMinimumLevel(LogLevel.Warning)
    .AddConfiguration(configuration.GetSection("Logging"))
    .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
    .AddSimpleConsole(console => console.SingleLine = true));
var usage = new UsageTracker();

try
{
    using IChatClient chatClient = ChatClientFactory.CreatePipeline(options, Environment.GetEnvironmentVariable, usage, loggerFactory);

    switch (command)
    {
        case ClassifyCommand:
            var classification = await new TicketClassifier(chatClient).ClassifyAsync(text);
            Console.WriteLine($"Setor: {classification.Department.ToString().ToUpperInvariant()}");
            Console.WriteLine($"Motivo: {classification.Reason}");
            PrintUsage(usage);
            return 0;

        case EvaluateCommand:
            return await EvaluateAsync(chatClient, options, usage, string.IsNullOrWhiteSpace(text) ? null : text.Trim());

        default:
            await foreach (var part in new KnowledgeAssistant(chatClient).AskStreamingAsync(text))
            {
                Console.Write(part);
            }

            Console.WriteLine();
            PrintUsage(usage);
            return 0;
    }
}
catch (LlmUnavailableException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 3;
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

static void PrintUsage(UsageTracker usage)
{
    if (usage.Calls.Count == 0)
    {
        return;
    }

    var tokens = usage.InputTokens + usage.OutputTokens == 0
        ? "o provedor não informou os tokens"
        : $"{usage.InputTokens} tokens de entrada e {usage.OutputTokens} de saída";
    var seconds = usage.Calls.Sum(call => call.Elapsed.TotalSeconds);
    Console.WriteLine($"[{usage.Calls.Count} chamada(s) · {tokens} · {seconds:0.0} s]");
}

static async Task<int> EvaluateAsync(IChatClient chatClient, LlmOptions options, UsageTracker usage, string? datasetPath)
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
    PrintUsage(usage);
    return 0;
}

/// <summary>Mostra o progresso na hora, na mesma thread, sem o contexto de sincronização do <see cref="Progress{T}"/>.</summary>
internal sealed class SynchronousProgress(Action<CaseResult> report) : IProgress<CaseResult>
{
    public void Report(CaseResult value) => report(value);
}
