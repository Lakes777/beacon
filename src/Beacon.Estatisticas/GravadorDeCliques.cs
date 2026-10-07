using Beacon.Contratos;
using Npgsql;
using NpgsqlTypes;

namespace Beacon.Estatisticas;

/// <summary>
/// Grava um clique na tabela clique (criada pelas migrações da API). SQL direto com Npgsql, sem
/// Entity Framework: é um INSERT só, e assim este serviço não depende das classes da API.
/// </summary>
public class GravadorDeCliques(NpgsqlDataSource banco)
{
    // ON CONFLICT (id) DO NOTHING: o RabbitMQ entrega "pelo menos uma vez", e a mesma mensagem pode
    // chegar de novo (ex.: a conexão caiu entre o INSERT e o ack). O segundo INSERT não faz nada.
    private const string Inserir = """
        INSERT INTO clique (id, codigo, momento, navegador, sistema, aparelho, origem, robo)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
        ON CONFLICT (id) DO NOTHING
        """;

    /// <returns>true se gravou; false se esse clique já estava gravado.</returns>
    public async Task<bool> GravarAsync(CliqueRegistrado clique, CancellationToken cancelar)
    {
        var classe = Classificador.Classificar(clique.UserAgent);

        await using var comando = banco.CreateCommand(Inserir);
        comando.Parameters.Add(new() { Value = clique.Id });
        comando.Parameters.Add(new() { Value = clique.Codigo });
        // O Postgres guarda timestamptz em UTC; o Npgsql só aceita DateTimeOffset com deslocamento zero
        comando.Parameters.Add(new() { Value = clique.Momento.ToUniversalTime() });
        comando.Parameters.Add(new() { Value = classe.Navegador });
        comando.Parameters.Add(new() { Value = classe.Sistema });
        comando.Parameters.Add(new() { Value = classe.Aparelho });
        // Com o tipo explícito: um null sozinho não diz ao Postgres o que ele é
        comando.Parameters.Add(new()
        {
            Value = (object?)Classificador.Origem(clique.Referer) ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Varchar,
        });
        comando.Parameters.Add(new() { Value = classe.Robo });

        return await comando.ExecuteNonQueryAsync(cancelar) == 1;
    }
}
