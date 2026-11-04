using Microsoft.Extensions.AI;

namespace Mimir.Core.Llm;

/// <summary>
/// Assistente que responde perguntas usando o modelo configurado.
/// </summary>
/// <remarks>
/// Depende apenas de <see cref="IChatClient"/>: trocar o Ollama local por uma API paga
/// é uma mudança de configuração, sem alterar esta classe.
/// </remarks>
public sealed class KnowledgeAssistant(IChatClient chatClient)
{
    private const string SystemPrompt =
        "Você é o Mimir, assistente da base de conhecimento de uma empresa. " +
        "Responda em português, de forma direta. Se não souber a resposta, diga que não sabe.";

    public async Task<string> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("A pergunta é obrigatória.", nameof(question));
        }

        ChatMessage[] messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, question.Trim()),
        ];

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        return response.Text;
    }
}
