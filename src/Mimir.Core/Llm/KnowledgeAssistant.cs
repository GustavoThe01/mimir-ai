using System.Runtime.CompilerServices;
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
    private static readonly string SystemPrompt = PromptLibrary.Load(PromptLibrary.Answer);

    public async Task<string> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var response = await chatClient.GetResponseAsync(BuildMessages(question), cancellationToken: cancellationToken);
        return response.Text;
    }

    /// <summary>
    /// Devolve a resposta aos poucos. Em um modelo local a resposta completa pode levar vários segundos,
    /// e ver o texto surgindo muda a experiência de quem espera.
    /// </summary>
    public IAsyncEnumerable<string> AskStreamingAsync(string question, CancellationToken cancellationToken = default)
    {
        // Valida aqui, e não dentro do iterador: assim o erro aparece na chamada, não na primeira leitura.
        var messages = BuildMessages(question);
        return StreamAsync(messages, cancellationToken);
    }

    private async IAsyncEnumerable<string> StreamAsync(
        ChatMessage[] messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    private static ChatMessage[] BuildMessages(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("A pergunta é obrigatória.", nameof(question));
        }

        return
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, question.Trim()),
        ];
    }
}
