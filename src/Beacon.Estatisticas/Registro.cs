using Npgsql;
using RabbitMQ.Client;

namespace Beacon.Estatisticas;

/// <summary>
/// Registra tudo o que o serviço precisa. Fica fora do Program.cs para os testes montarem o mesmo
/// serviço, só trocando as conexões pelas dos contêineres.
/// </summary>
public static class Registro
{
    public static IHostApplicationBuilder AdicionarEstatisticas(this IHostApplicationBuilder builder)
    {
        // Melhor nem subir do que subir e não gravar nada
        var banco = builder.Configuration.GetConnectionString("Banco")
            ?? throw new InvalidOperationException(
                "Defina a conexão com o banco em ConnectionStrings:Banco (variável ConnectionStrings__Banco).");
        var rabbit = builder.Configuration.GetConnectionString("RabbitMQ")
            ?? throw new InvalidOperationException(
                "Defina a conexão com o RabbitMQ em ConnectionStrings:RabbitMQ (variável ConnectionStrings__RabbitMQ).");

        // Um "data source" só para o programa inteiro: ele mantém o pool de conexões com o Postgres
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(banco));
        builder.Services.AddSingleton<GravadorDeCliques>();

        builder.Services.AddSingleton(new ConnectionFactory
        {
            Uri = new Uri(rabbit),
            // Aparece no painel do RabbitMQ, para saber quem é cada conexão
            ClientProvidedName = "beacon-estatisticas",
            // Reconecta sozinho e refaz filas e consumidores se a conexão cair (já é o padrão; fica explícito)
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            // Uma mensagem por vez: a ordem de ack/nack fica simples e o banco não recebe rajadas
            ConsumerDispatchConcurrency = 1,
        });
        builder.Services.AddHostedService<ConsumidorDeCliques>();

        return builder;
    }
}
