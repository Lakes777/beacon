using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Beacon.Api.Cache;

/// <summary>
/// Redis fora do ar deixa a API "Degraded" (mais lenta), e não "Unhealthy": os links continuam
/// funcionando pelo banco, então não faz sentido o Docker reiniciar a API por causa disso.
/// </summary>
public class SaudeDoRedis(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext contexto, CancellationToken cancelar = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception erro) when (CacheDeLinks.FalhaDoRedis(erro))
        {
            return HealthCheckResult.Degraded("Redis indisponível: os links vão direto ao banco.", erro);
        }
    }
}
