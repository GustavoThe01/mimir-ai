using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Mimir.Core.Llm;

public enum Department
{
    Financeiro,
    Ti,
    Rh,
    Juridico,
    Outros,
}

public sealed record TicketClassification(Department Department, string Reason);

/// <summary>O modelo não devolveu uma resposta válida, mesmo depois da nova tentativa.</summary>
public sealed class StructuredOutputException : Exception
{
    public StructuredOutputException(string message)
        : base(message)
    {
    }

    public StructuredOutputException()
    {
    }

    public StructuredOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Classifica um chamado pelo setor responsável, usando saída estruturada.
/// </summary>
/// <remarks>
/// O restante do sistema recebe um tipo C#, nunca texto do modelo. Uma resposta inválida ganha uma nova tentativa,
/// porque modelos pequenos erram de forma não determinística. Se falhar de novo, o erro é explícito:
/// um setor inventado nunca chega a virar resultado.
/// </remarks>
public sealed class TicketClassifier(IChatClient chatClient)
{
    private const int MaxAttempts = 2;

    private static readonly string SystemPrompt = PromptLibrary.Load(PromptLibrary.TicketClassification);
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>(
        PromptLibrary.Load(PromptLibrary.TicketClassificationSchema));

    public async Task<TicketClassification> ClassifyAsync(string ticket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            throw new ArgumentException("O texto do chamado é obrigatório.", nameof(ticket));
        }

        ChatMessage[] messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"<chamado>{ticket.Trim()}</chamado>"),
        ];

        var options = new ChatOptions
        {
            MaxOutputTokens = 256,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "ticket_classification"),
        };

        var problems = string.Empty;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
            if (TryParse(response.Text, out var classification, out problems))
            {
                return classification;
            }
        }

        throw new StructuredOutputException($"O modelo não devolveu uma classificação válida: {problems}");
    }

    private static bool TryParse(string text, out TicketClassification classification, out string problems)
    {
        classification = null!;

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            problems = "a resposta não é um JSON.";
            return false;
        }

        var errors = JsonSchemaValidator.Validate(json, Schema);
        if (errors.Count > 0)
        {
            problems = string.Join(" ", errors);
            return false;
        }

        classification = new TicketClassification(
            Enum.Parse<Department>(json.GetProperty("department").GetString()!, ignoreCase: true),
            json.GetProperty("reason").GetString()!.Trim());
        problems = string.Empty;
        return true;
    }
}
