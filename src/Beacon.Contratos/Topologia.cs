using RabbitMQ.Client;

namespace Beacon.Contratos;

/// <summary>
/// Nomes e declaração das filas no RabbitMQ. Os dois lados declaram tudo ao conectar (declarar o
/// que já existe com os mesmos parâmetros não faz nada): assim não importa quem sobe primeiro.
///
/// API --publica--> exchange "beacon.cliques" --chave "clique"--> fila "beacon.estatisticas" --> consumidor
///                                                                   |
///                       mensagem rejeitada (inválida) --> "beacon.cliques.mortos" --> fila "beacon.estatisticas.mortos"
/// </summary>
public static class Topologia
{
    public const string Exchange = "beacon.cliques";
    public const string ChaveDoClique = "clique";
    public const string Fila = "beacon.estatisticas";

    /// <summary>"Dead letter": para onde vão as mensagens que o consumidor rejeita, para olhar depois.</summary>
    public const string ExchangeDosMortos = "beacon.cliques.mortos";
    public const string FilaDosMortos = "beacon.estatisticas.mortos";

    public static async Task DeclararAsync(IChannel canal, CancellationToken cancelar = default)
    {
        // durable: as filas e as mensagens persistentes sobrevivem a um reinício do RabbitMQ
        await canal.ExchangeDeclareAsync(ExchangeDosMortos, ExchangeType.Fanout, durable: true, cancellationToken: cancelar);
        await canal.QueueDeclareAsync(FilaDosMortos, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancelar);
        await canal.QueueBindAsync(FilaDosMortos, ExchangeDosMortos, routingKey: "", cancellationToken: cancelar);

        await canal.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: cancelar);
        await canal.QueueDeclareAsync(Fila, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = ExchangeDosMortos },
            cancellationToken: cancelar);
        await canal.QueueBindAsync(Fila, Exchange, ChaveDoClique, cancellationToken: cancelar);
    }
}
