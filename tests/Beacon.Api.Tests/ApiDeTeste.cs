using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

// Um Postgres e uma API para todos os testes do projeto (subir o contêiner leva alguns segundos)
[assembly: AssemblyFixture(typeof(Beacon.Api.Tests.ApiDeTeste))]

namespace Beacon.Api.Tests;

/// <summary>
/// Sobe a API inteira, em memória, ligada a um Postgres de verdade num contêiner (Testcontainers).
/// É o equivalente ao @SpringBootTest + Testcontainers do Vigil.
/// </summary>
public class ApiDeTeste : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer banco = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").Build();

    /// <summary>Para os testes que simulam o Redis fora do ar usarem o mesmo banco.</summary>
    public string ConexaoDoBanco => banco.GetConnectionString();

    public async ValueTask InitializeAsync() => await Task.WhenAll(banco.StartAsync(), redis.StartAsync());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ambiente próprio: sem ele, a API leria o appsettings.Development.json (o banco do dia a dia).
        // Se a conexão abaixo deixar de valer, a API nem sobe e o teste quebra na hora.
        builder.UseEnvironment("Testes");
        builder.UseSetting("ConnectionStrings:Banco", banco.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", redis.GetConnectionString());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await banco.DisposeAsync();
        await redis.DisposeAsync();
    }
}
