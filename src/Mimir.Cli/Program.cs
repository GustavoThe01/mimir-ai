using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mimir.Core.Llm;
using Mimir.Infrastructure.Llm;

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
    Console.WriteLine(await assistant.AskAsync(string.Join(' ', args)));
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
