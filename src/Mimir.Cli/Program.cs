using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mimir.Core.Llm;
using Mimir.Infrastructure.Llm;

// Sem isso, os acentos da resposta saem errados no terminal do Windows.
Console.OutputEncoding = Encoding.UTF8;

const string ClassifyCommand = "classificar";

var isClassification = args.Length > 1 && args[0] == ClassifyCommand;
var text = string.Join(' ', isClassification ? args[1..] : args);

if (string.IsNullOrWhiteSpace(text) || (args.Length == 1 && args[0] == ClassifyCommand))
{
    Console.WriteLine("Uso:");
    Console.WriteLine("  mimir \"sua pergunta\"");
    Console.WriteLine($"  mimir {ClassifyCommand} \"texto do chamado\"");
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

    if (isClassification)
    {
        var classification = await new TicketClassifier(chatClient).ClassifyAsync(text);
        Console.WriteLine($"Setor: {classification.Department.ToString().ToUpperInvariant()}");
        Console.WriteLine($"Motivo: {classification.Reason}");
        return 0;
    }

    await foreach (var part in new KnowledgeAssistant(chatClient).AskStreamingAsync(text))
    {
        Console.Write(part);
    }

    Console.WriteLine();
    return 0;
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
