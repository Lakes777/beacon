using StackExchange.Redis;

namespace Beacon.Api.Cache;

/// <summary>
/// O que o cache sabe sobre um código. Achou = false: o cache não sabe (vá ao banco).
/// Achou = true e Destino = null: o cache sabe que o link não existe (ou está desativado).
/// </summary>
public readonly record struct NoCache(bool Achou, string? Destino)
{
    public static readonly NoCache NaoSabe = new(false, null);
}

/// <summary>
/// Cache do redirecionamento no Redis ("cache-aside"): quem lê consulta aqui primeiro e, se não
/// achar, vai ao banco e guarda o resultado; quem muda um link apaga a cópia daqui.
/// Se o Redis cair, nada quebra: todo método engole o erro e o redirecionamento vai ao banco.
/// </summary>
public class CacheDeLinks(IConnectionMultiplexer redis, ILogger<CacheDeLinks> log)
{
    /// <summary>
    /// Quanto uma cópia vale. A invalidação apaga a cópia na hora; o prazo é a rede de segurança
    /// para o caso raro em que a cópia velha volta (ver README, "Cache").
    /// </summary>
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(10);

    /// <summary>"Não existe" vale pouco: um link novo com esse código precisa funcionar logo.</summary>
    public static readonly TimeSpan ValidadeDoInexistente = TimeSpan.FromMinutes(1);

    // Marca de "não existe" (um destino nunca é vazio)
    private const string Inexistente = "";

    public static RedisKey Chave(string codigo) => $"beacon:link:{codigo}";

    public async Task<NoCache> Buscar(string codigo)
    {
        try
        {
            var valor = await redis.GetDatabase().StringGetAsync(Chave(codigo));
            if (valor.IsNull)
            {
                return NoCache.NaoSabe;
            }
            return new NoCache(true, valor == Inexistente ? null : valor.ToString());
        }
        catch (Exception erro) when (FalhaDoRedis(erro))
        {
            // Debug, não Warning: com o Redis caído seriam avisos a cada clique. A queda e a volta
            // da conexão são avisadas uma vez só (ver Program.cs)
            log.LogDebug("Redis indisponível ao buscar {Codigo}; indo ao banco: {Erro}", codigo, erro.Message);
            return NoCache.NaoSabe;
        }
    }

    /// <param name="destino">null guarda "não existe".</param>
    public async Task Guardar(string codigo, string? destino)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(Chave(codigo), destino ?? Inexistente,
                destino is null ? ValidadeDoInexistente : Validade);
        }
        catch (Exception erro) when (FalhaDoRedis(erro))
        {
            log.LogDebug("Redis indisponível ao guardar {Codigo}: {Erro}", codigo, erro.Message);
        }
    }

    /// <summary>Chamado depois de criar, editar ou apagar o link no banco.</summary>
    public async Task Esquecer(string codigo)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(Chave(codigo));
        }
        catch (Exception erro) when (FalhaDoRedis(erro))
        {
            // Aviso de verdade: a cópia velha fica valendo até a Validade acabar
            log.LogWarning("Redis indisponível ao esquecer {Codigo}; a cópia antiga vale até expirar: {Erro}",
                codigo, erro.Message);
        }
    }

    // O tempo esgotado do Redis não herda de RedisException
    internal static bool FalhaDoRedis(Exception erro) => erro is RedisException or TimeoutException;
}
