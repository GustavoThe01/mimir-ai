namespace Mimir.Core.Knowledge;

/// <summary>
/// Os perfis de quem pergunta. Um trecho só pode ser lido se o documento dele for de <c>todos</c> ou de algum
/// desses perfis.
/// </summary>
/// <remarks>
/// O filtro acontece antes do prompt, e não depois da resposta: um trecho que a pessoa não pode ler nunca é enviado
/// ao modelo. Pedir ao modelo para "não revelar" um documento seria confiar nele para guardar segredo, e um prompt
/// bem montado consegue tirar qualquer coisa que esteja no contexto.
/// </remarks>
public sealed class AccessScope
{
    public const string Everyone = "todos";

    private readonly HashSet<string> _profiles;

    private AccessScope(IEnumerable<string> profiles)
    {
        _profiles = new HashSet<string>(profiles.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0), StringComparer.Ordinal)
        {
            Everyone,
        };
    }

    /// <summary>Quem não tem perfil nenhum: só lê documentos de <c>todos</c>.</summary>
    public static AccessScope Public { get; } = new([]);

    public IReadOnlySet<string> Profiles => _profiles;

    /// <exception cref="ArgumentException">Perfil que não existe na base, para um erro de digitação não virar acesso negado silencioso.</exception>
    public static AccessScope For(params string[] profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var unknown = profiles.Select(p => p.Trim().ToLowerInvariant()).Where(p => !DocumentParser.KnownProfiles.Contains(p)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Perfil desconhecido: {string.Join(", ", unknown)}. Use: {string.Join(", ", DocumentParser.KnownProfiles.Order())}.", nameof(profiles));
        }

        return new AccessScope(profiles);
    }

    public bool CanRead(KnowledgeChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.Access.Any(_profiles.Contains);
    }

    public override string ToString() => string.Join(", ", _profiles.Order());
}
