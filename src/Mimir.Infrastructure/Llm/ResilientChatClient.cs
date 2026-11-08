using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Mimir.Infrastructure.Llm;

/// <summary>O provedor não respondeu, mesmo depois das novas tentativas. A mensagem já vem pronta para o usuário.</summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LlmUnavailableException(string message)
        : base(message)
    {
    }

    public LlmUnavailableException()
    {
    }
}

/// <summary>
/// Middleware de resiliência: limite de tempo por tentativa e nova tentativa, com espera crescente,
/// só para falhas passageiras.
/// </summary>
/// <remarks>
/// <para>
/// Passageira é a falha que pode dar certo na próxima vez: conexão recusada, tempo esgotado, limite de requisições
/// (429) e erro do servidor (5xx). Chave inválida ou modelo inexistente falham na hora, sem nova tentativa:
/// repetir só atrasaria o erro e, numa API paga, poderia gerar custo.
/// </para>
/// <para>
/// No streaming, a nova tentativa só acontece antes do primeiro trecho. Depois disso, quem chamou já mostrou
/// parte da resposta, e recomeçar duplicaria o texto.
/// </para>
/// </remarks>
public sealed partial class ResilientChatClient(
    IChatClient innerClient,
    ResilienceSettings settings,
    TimeProvider timeProvider,
    ILogger logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);
            try
            {
                return await base.GetResponseAsync(list, options, timeout.Token);
            }
            catch (Exception exception) when (ShouldRetry(exception, attempt, cancellationToken, out var reason))
            {
                await WaitBeforeRetryAsync(attempt, reason, cancellationToken);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                throw Unavailable(exception, attempt);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);
            await using var stream = base.GetStreamingResponseAsync(list, options, timeout.Token).GetAsyncEnumerator(timeout.Token);

            // Primeiro trecho: até aqui ainda dá para tentar de novo.
            bool hasFirst;
            Exception? failure = null;
            try
            {
                hasFirst = await stream.MoveNextAsync();
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                hasFirst = false;
                failure = exception;
            }

            if (failure is not null)
            {
                if (attempt >= settings.MaxAttempts)
                {
                    throw Unavailable(failure, attempt);
                }

                await WaitBeforeRetryAsync(attempt, Describe(failure, cancellationToken), cancellationToken);
                continue;
            }

            if (!hasFirst)
            {
                yield break;
            }

            yield return stream.Current;

            // Resposta já começou: o limite de tempo deixa de valer, porque o modelo está produzindo.
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            while (await stream.MoveNextAsync())
            {
                yield return stream.Current;
            }

            yield break;
        }
    }

    private bool ShouldRetry(Exception exception, int attempt, CancellationToken callerToken, out string reason)
    {
        reason = Describe(exception, callerToken);
        return attempt < settings.MaxAttempts && IsTransient(exception, callerToken);
    }

    private async Task WaitBeforeRetryAsync(int attempt, string reason, CancellationToken cancellationToken)
    {
        // Espera exponencial: 1x, 2x, 4x o intervalo base. Dá tempo ao provedor de se recuperar.
        var delay = settings.BaseDelay * Math.Pow(2, attempt - 1);
        LogRetry(logger, attempt, settings.MaxAttempts, reason, delay.TotalSeconds);
        await Task.Delay(delay, timeProvider, cancellationToken);
    }

    private LlmUnavailableException Unavailable(Exception exception, int attempts) =>
        new($"{settings.ProviderName} não respondeu depois de {attempts} tentativa(s): {Describe(exception, CancellationToken.None)}. {settings.Hint}",
            exception);

    /// <summary>A falha pode dar certo numa nova tentativa? Cancelamento pedido por quem chamou nunca é repetido.</summary>
    internal static bool IsTransient(Exception exception, CancellationToken callerToken) => exception switch
    {
        OperationCanceledException => !callerToken.IsCancellationRequested,
        TimeoutException => true,
        HttpRequestException http => http.StatusCode is null || IsTransientStatus((int)http.StatusCode),
        ClientResultException client => client.Status == 0 || IsTransientStatus(client.Status),
        AnthropicRateLimitException or Anthropic5xxException or AnthropicIOException => true,
        _ => false,
    };

    private static bool IsTransientStatus(int status) =>
        status is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests or >= 500;

    private static string Describe(Exception exception, CancellationToken callerToken) => exception switch
    {
        OperationCanceledException when !callerToken.IsCancellationRequested => "tempo esgotado",
        HttpRequestException { StatusCode: null } => "não foi possível conectar",
        HttpRequestException http => $"HTTP {(int)http.StatusCode!}",
        ClientResultException client => $"HTTP {client.Status}",
        AnthropicRateLimitException => "limite de requisições atingido",
        _ => exception.GetType().Name,
    };

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Tentativa {Attempt} de {MaxAttempts} falhou ({Reason}). Nova tentativa em {DelaySeconds:0.#} s")]
    private static partial void LogRetry(ILogger logger, int attempt, int maxAttempts, string reason, double delaySeconds);
}

/// <summary>Parâmetros da resiliência, montados a partir de <see cref="LlmOptions"/>.</summary>
public sealed record ResilienceSettings(
    TimeSpan Timeout,
    int MaxAttempts,
    TimeSpan BaseDelay,
    string ProviderName,
    string Hint);
