using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mimir.Core.Evaluation;
using Mimir.Core.Knowledge;
using Mimir.Core.Llm;
using Mimir.Infrastructure.Knowledge;
using Mimir.Infrastructure.Llm;

// Sem isso, os acentos da resposta saem errados no terminal do Windows.
Console.OutputEncoding = Encoding.UTF8;

const string ClassifyCommand = "classificar";
const string EvaluateCommand = "avaliar";
const string IngestCommand = "ingerir";
const string SearchCommand = "buscar";
const string EvaluateSearchCommand = "avaliar-busca";
const string DefaultSearchDataset = "datasets/busca.v1.jsonl";
const string DefaultDataset = "datasets/classificacao-chamados.v1.jsonl";
const string DefaultKnowledge = "knowledge";

var command = args.Length > 0 && args[0] is ClassifyCommand or EvaluateCommand or IngestCommand or SearchCommand or EvaluateSearchCommand ? args[0] : null;
var text = string.Join(' ', command is null ? args : args[1..]);

var settings = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

// Ingestão e busca usam o modelo de embeddings, não o modelo de conversa.
if (command is IngestCommand or SearchCommand or EvaluateSearchCommand)
{
    var embeddingOptions = settings.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new EmbeddingOptions();
    try
    {
        var (generator, model) = EmbeddingGeneratorFactory.Create(embeddingOptions, Environment.GetEnvironmentVariable);
        using (generator)
        {
            var store = new SqliteChunkStore(embeddingOptions.Database);
            if (command == IngestCommand)
            {
                return await IngestAsync(
                    string.IsNullOrWhiteSpace(text) ? Path.Combine(AppContext.BaseDirectory, DefaultKnowledge) : text.Trim(),
                    new KnowledgeIndexer(generator, store, model));
            }

            var search = new HybridSearch(generator, store, model);
            if (command == EvaluateSearchCommand)
            {
                return await EvaluateSearchAsync(search, string.IsNullOrWhiteSpace(text) ? null : text.Trim(), model);
            }

            var (mode, question) = ParseSearchArguments(args[1..]);
            if (string.IsNullOrWhiteSpace(question))
            {
                Console.Error.WriteLine($"Uso: mimir {SearchCommand} [--modo hibrida|vetor|texto] \"sua pergunta\"");
                return 1;
            }

            return await SearchAsync(question, mode, search);
        }
    }
    catch (Exception exception) when (exception is FormatException or IOException or InvalidOperationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 5;
    }
    catch (HttpRequestException exception)
    {
        Console.Error.WriteLine($"Não consegui gerar os vetores com {embeddingOptions.Provider} em {embeddingOptions.Endpoint}: {exception.Message}");
        Console.Error.WriteLine($"Confira se o Ollama está rodando e se o modelo foi baixado: ollama pull {embeddingOptions.Model}");
        return 3;
    }
}

if (command != EvaluateCommand && string.IsNullOrWhiteSpace(text))
{
    Console.WriteLine("Uso:");
    Console.WriteLine("  mimir \"sua pergunta\"");
    Console.WriteLine($"  mimir {ClassifyCommand} \"texto do chamado\"");
    Console.WriteLine($"  mimir {EvaluateCommand} [arquivo.jsonl]   (padrão: {DefaultDataset})");
    Console.WriteLine($"  mimir {IngestCommand} [pasta]            (padrão: {DefaultKnowledge})");
    Console.WriteLine($"  mimir {SearchCommand} [--modo hibrida|vetor|texto] \"sua pergunta\"");
    Console.WriteLine($"  mimir {EvaluateSearchCommand} [arquivo.jsonl]   (padrão: {DefaultSearchDataset})");
    return 1;
}

var configuration = settings;

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

static async Task<int> IngestAsync(string folder, KnowledgeIndexer indexer)
{
    var documents = await FileDocumentSource.LoadAsync(folder);
    var chunks = documents.SelectMany(document => Chunker.Split(document)).ToList();

    Console.WriteLine($"{"Documento",-28} {"Setor",-11} {"Acesso",-20} {"Trechos",7}");
    foreach (var document in documents)
    {
        var count = chunks.Count(chunk => chunk.DocumentId == document.Id);
        Console.WriteLine($"{document.Id,-28} {document.Department.ToString().ToUpperInvariant(),-11} {string.Join(", ", document.Access),-20} {count,7}");
    }

    var sizes = chunks.Select(chunk => chunk.Text.Length).ToList();
    Console.WriteLine();
    Console.WriteLine($"{documents.Count} documentos, {chunks.Count} trechos, de {sizes.Min()} a {sizes.Max()} caracteres (média {sizes.Average():0}).");
    Console.WriteLine($"Exemplo de citação: {chunks[0].Citation} ({chunks[0].Section})");

    var report = await indexer.IndexAsync(chunks);
    Console.WriteLine();
    var dimensions = report.Embedded > 0 ? $", {report.Dimensions} dimensões" : string.Empty;
    Console.WriteLine($"Índice ({report.Model}): {report.Embedded} trecho(s) com vetor novo, {report.Unchanged} sem mudança, "
        + $"{report.Removed} removido(s){dimensions}, {report.Elapsed.TotalSeconds:0.0} s.");
    return 0;
}

static (SearchMode Mode, string Question) ParseSearchArguments(string[] arguments)
{
    if (arguments.Length >= 2 && arguments[0] == "--modo")
    {
        var mode = arguments[1] switch
        {
            "vetor" => SearchMode.Vector,
            "texto" => SearchMode.Text,
            "hibrida" => SearchMode.Hybrid,
            _ => throw new InvalidOperationException($"Modo desconhecido: {arguments[1]}. Use hibrida, vetor ou texto."),
        };
        return (mode, string.Join(' ', arguments[2..]));
    }

    return (SearchMode.Hybrid, string.Join(' ', arguments));
}

static async Task<int> SearchAsync(string query, SearchMode mode, HybridSearch search)
{
    var hits = await search.SearchAsync(query, mode);
    Console.WriteLine($"{"RRF",-7} {"Vetor",5} {"Texto",5}  Trecho");
    foreach (var hit in hits)
    {
        Console.WriteLine($"{hit.Score:0.0000}  {Rank(hit.VectorRank),5} {Rank(hit.TextRank),5}  {hit.Chunk.Citation,-38} {hit.Chunk.Section}");
    }

    return 0;

    static string Rank(int? rank) => rank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
}

static async Task<int> EvaluateSearchAsync(HybridSearch search, string? datasetPath, string model)
{
    var path = datasetPath ?? Path.Combine(AppContext.BaseDirectory, DefaultSearchDataset);
    var cases = SearchEvaluation.Parse(await File.ReadAllTextAsync(path));
    Console.WriteLine($"Avaliando {cases.Count} perguntas com {model}...");
    Console.WriteLine();
    Console.WriteLine($"{"Busca",-8} {"Acerto no 1º",13} {"Entre os 3",11} {"MRR",6}  Errou o 1º");
    foreach (var score in await SearchEvaluation.RunAsync(search, cases))
    {
        var name = score.Mode switch { SearchMode.Vector => "vetor", SearchMode.Text => "texto", _ => "híbrida" };
        Console.WriteLine($"{name,-8} {score.HitAt1,7} de {score.Cases,-3} {score.HitAt3,5} de {score.Cases,-3} {score.Mrr,6:0.00}  {string.Join(", ", score.Missed)}");
    }

    return 0;
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
