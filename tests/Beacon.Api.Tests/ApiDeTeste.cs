using Beacon.Api.Mensageria;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

// Um Postgres e uma API para todos os testes do projeto (subir o contêiner leva alguns segundos)
[assembly: AssemblyFixture(typeof(Beacon.Api.Tests.ApiDeTeste))]

namespace Beacon.Api.Tests;

/// <summary>
/// Sobe a API inteira, em memória, ligada a um Postgres, um Redis e um RabbitMQ de verdade, em
/// contêineres (Testcontainers). É o equivalente ao @SpringBootTest + Testcontainers do Vigil.
/// </summary>
public class ApiDeTeste : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer banco = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").Build();
    // Sem o painel de administração (o "-management" do compose): os testes não precisam dele
    private readonly RabbitMqContainer rabbitmq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    /// <summary>Para os testes que simulam o Redis fora do ar usarem o mesmo banco.</summary>
    public string ConexaoDoBanco => banco.GetConnectionString();

    /// <summary>Para os testes lerem a fila direto no RabbitMQ.</summary>
    public string ConexaoDoRabbitMQ => rabbitmq.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(banco.StartAsync(), redis.StartAsync(), rabbitmq.StartAsync());

        // A API conecta ao RabbitMQ em segundo plano. Espera a conexão abrir: senão o primeiro
        // teste do /saude poderia ver "Degraded" só por ter chegado antes dela
        var publicador = Services.GetRequiredService<PublicadorDeCliques>();
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (!publicador.Conectado)
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException("A API não conectou ao RabbitMQ do teste em 30 s.");
            }
            await Task.Delay(50);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ambiente próprio: sem ele, a API leria o appsettings.Development.json (o banco do dia a dia).
        // Se a conexão abaixo deixar de valer, a API nem sobe e o teste quebra na hora.
        builder.UseEnvironment("Testes");
        builder.UseSetting("ConnectionStrings:Banco", banco.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", redis.GetConnectionString());
        builder.UseSetting("ConnectionStrings:RabbitMQ", rabbitmq.GetConnectionString());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await banco.DisposeAsync();
        await redis.DisposeAsync();
        await rabbitmq.DisposeAsync();
    }
}
