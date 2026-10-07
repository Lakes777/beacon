using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Beacon.Api.Mensageria;

/// <summary>
/// RabbitMQ fora do ar deixa a API "Degraded", e não "Unhealthy": os links continuam redirecionando
/// e os cliques esperam na memória, então não faz sentido o Docker reiniciar a API por causa disso
/// (reiniciar, aliás, perderia os cliques guardados).
/// </summary>
public class SaudeDoRabbitMQ(PublicadorDeCliques publicador, FilaDeCliques fila) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext contexto, CancellationToken cancelar = default) =>
        Task.FromResult(publicador.Conectado
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"RabbitMQ indisponível: {fila.Pendentes} cliques esperando na memória."));
}
