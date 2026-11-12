namespace Mimir.Core.Llm;

/// <summary>
/// Lê os prompts versionados de <c>Prompts/</c>, embutidos no assembly.
/// </summary>
/// <remarks>
/// Prompt é parte do comportamento do sistema, então é tratado como código: fica em arquivo, passa por revisão
/// e tem versão no nome. Para mudar um prompt, crio o <c>v2</c> e posso comparar os dois na avaliação do Dia 04.
/// </remarks>
public static class PromptLibrary
{
    public const string Answer = "answer.v1.txt";
    public const string GroundedAnswer = "grounded-answer.v1.txt";
    public const string GroundedAnswerSchema = "grounded-answer.v1.schema.json";
    public const string TicketClassification = "ticket-classification.v1.txt";
    public const string TicketClassificationSchema = "ticket-classification.v1.schema.json";

    public static string Load(string name)
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var resource = $"{assembly.GetName().Name}.Prompts.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Prompt não encontrado: {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
