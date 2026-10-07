using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

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

    public async ValueTask InitializeAsync() => await banco.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ambiente próprio: sem ele, a API leria o appsettings.Development.json (o banco do dia a dia).
        // Se a conexão abaixo deixar de valer, a API nem sobe e o teste quebra na hora.
        builder.UseEnvironment("Testes");
        builder.UseSetting("ConnectionStrings:Banco", banco.GetConnectionString());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await banco.DisposeAsync();
    }
}
