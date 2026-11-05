using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mimir.Core.Llm;
using Mimir.Infrastructure.Llm;

// Sem isso, os acentos da resposta saem errados no terminal do Windows.
Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("Uso: mimir \"sua pergunta\"");
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
    var assistant = new KnowledgeAssistant(chatClient);
    await foreach (var part in assistant.AskStreamingAsync(string.Join(' ', args)))
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
