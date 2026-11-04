namespace Mimir.Infrastructure.Llm;

public enum LlmProvider
{
    /// <summary>Modelo local, sem custo. É o padrão do projeto.</summary>
    Ollama = 1,

    /// <summary>API paga da Anthropic (Claude). Exige a variável ANTHROPIC_API_KEY.</summary>
    Anthropic = 2,

    /// <summary>API paga da OpenAI ou serviço compatível via Endpoint. Exige a variável OPENAI_API_KEY.</summary>
    OpenAI = 3,

    /// <summary>Resposta fixa, sem rede. Usado em testes e no CI.</summary>
    Fake = 4,
}

/// <summary>Seção "Llm" da configuração. A chave de API nunca fica aqui: vem de variável de ambiente.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    public string? Model { get; set; }

    /// <summary>Endereço do serviço. Obrigatório no Ollama; opcional na OpenAI, para serviços compatíveis.</summary>
    public string? Endpoint { get; set; }
}
