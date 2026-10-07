using Beacon.Api.Banco;
using Beacon.Contratos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

// Um Postgres, um RabbitMQ e um serviço para todos os testes do projeto (subir contêiner leva segundos)
[assembly: AssemblyFixture(typeof(Beacon.Estatisticas.Tests.ServicoDeTeste))]

namespace Beacon.Estatisticas.Tests;

/// <summary>
/// Sobe o serviço de estatísticas de verdade (o mesmo AdicionarEstatisticas do Program.cs), ligado a
/// um Postgres e a um RabbitMQ em contêineres. A tabela clique vem das migrações da API, como em produção.
/// </summary>
public class ServicoDeTeste : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private IHost? servico;
    private IConnection? conexao;

    public NpgsqlDataSource Banco { get; private set; } = null!;

    /// <summary>Um canal para os testes publicarem e lerem filas, como a API faria.</summary>
    public IChannel Canal { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync());

        var opcoes = new DbContextOptionsBuilder<BeaconContexto>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var contexto = new BeaconContexto(opcoes))
        {
            await contexto.Database.MigrateAsync();
        }

        Banco = NpgsqlDataSource.Create(postgres.GetConnectionString());

        // Construtor "vazio": não lê o appsettings.Development.json (o banco do dia a dia)
        var builder = Host.CreateEmptyApplicationBuilder(new() { EnvironmentName = "Testes" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Banco"] = postgres.GetConnectionString(),
            ["ConnectionStrings:RabbitMQ"] = rabbit.GetConnectionString(),
        });
        builder.AdicionarEstatisticas();
        servico = builder.Build();
        await servico.StartAsync();

        conexao = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync();
        Canal = await conexao.CreateChannelAsync();
        // Como a API faz: declara ao conectar, sem depender de o serviço já ter declarado
        await Topologia.DeclararAsync(Canal);
    }

    public async ValueTask DisposeAsync()
    {
        if (servico is not null)
        {
            await servico.StopAsync();
            servico.Dispose();
        }

        if (conexao is not null)
        {
            await Canal.DisposeAsync();
            await conexao.DisposeAsync();
        }

        if (Banco is not null)
        {
            await Banco.DisposeAsync();
        }

        await Task.WhenAll(postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask());
    }
}
