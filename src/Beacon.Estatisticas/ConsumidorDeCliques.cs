using Beacon.Contratos;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Beacon.Estatisticas;

/// <summary>
/// Lê a fila de cliques e grava cada um no Postgres. O ack (a confirmação para o RabbitMQ apagar a
/// mensagem) só sai DEPOIS de gravar: se o serviço cair no meio, a mensagem volta para a fila e
/// ninguém perde clique. Se ela chegar duas vezes, o INSERT com ON CONFLICT não conta em dobro.
/// </summary>
public class ConsumidorDeCliques(
    ConnectionFactory fabrica,
    GravadorDeCliques gravador,
    ILogger<ConsumidorDeCliques> log) : BackgroundService
{
    /// <summary>
    /// Quantas mensagens o RabbitMQ manda sem esperar ack. Algumas, para não esperar a rede a cada
    /// clique; poucas, para um serviço que caiu não levar um monte de mensagens "presas" com ele.
    /// </summary>
    public const ushort Prefetch = 20;

    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

    // O RabbitMQ entrega uma mensagem por vez a este consumidor (ConsumerDispatchConcurrency = 1),
    // então estes campos não são disputados. O semáforo existe para o desligamento esperar a mensagem
    // em andamento terminar.
    private readonly SemaphoreSlim emAndamento = new(1, 1);
    private volatile bool parando;
    private int falhasDoBanco;
    private CancellationToken parar;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        parar = stoppingToken;
        await using var conexao = await ConectarAsync(stoppingToken);
        conexao.ConnectionShutdownAsync += (_, e) =>
        {
            if (!parando)
            {
                log.LogWarning("Conexão com o RabbitMQ caiu ({Motivo}); tentando reconectar", e.ReplyText);
            }

            return Task.CompletedTask;
        };
        conexao.RecoverySucceededAsync += (_, _) =>
        {
            log.LogInformation("Conexão com o RabbitMQ voltou");
            return Task.CompletedTask;
        };

        await using var canal = await conexao.CreateChannelAsync(cancellationToken: stoppingToken);
        await Topologia.DeclararAsync(canal, stoppingToken);
        await canal.BasicQosAsync(prefetchSize: 0, prefetchCount: Prefetch, global: false, stoppingToken);

        var consumidor = new AsyncEventingBasicConsumer(canal);
        consumidor.ReceivedAsync += (_, entrega) => ReceberAsync(canal, entrega);
        var etiqueta = await canal.BasicConsumeAsync(Topologia.Fila, autoAck: false, consumidor, stoppingToken);
        log.LogInformation("Consumindo a fila {Fila}", Topologia.Fila);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Pediram para desligar: segue para o desligamento gracioso
        }

        await DesligarAsync(canal, etiqueta);
    }

    /// <summary>
    /// Para de receber e espera a mensagem em andamento terminar (com o ack dela). As que já tinham
    /// chegado e não foram processadas ficam sem ack e o RabbitMQ as devolve à fila ao fechar o canal.
    /// </summary>
    private async Task DesligarAsync(IChannel canal, string etiqueta)
    {
        parando = true;
        try
        {
            await canal.BasicCancelAsync(etiqueta);
        }
        catch (Exception erro) when (erro is AlreadyClosedException or OperationInterruptedException)
        {
            // Canal já fechado: nada a cancelar
        }

        await emAndamento.WaitAsync();
        emAndamento.Release();
        log.LogInformation("Consumidor parado");
    }

    private async Task<IConnection> ConectarAsync(CancellationToken cancelar)
    {
        // Se o RabbitMQ ainda não subiu (ex.: os dois sobem juntos no Docker), tenta de novo com
        // espera crescente em vez de cair. Depois de conectado, a recuperação automática cuida das quedas.
        var espera = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return await fabrica.CreateConnectionAsync(cancelar);
            }
            catch (BrokerUnreachableException erro)
            {
                log.LogWarning("RabbitMQ fora do ar ({Erro}); nova tentativa em {Segundos} s",
                    erro.GetBaseException().Message, espera.TotalSeconds);
                await Task.Delay(espera, cancelar);
                espera = Dobrar(espera);
            }
        }
    }

    private async Task ReceberAsync(IChannel canal, BasicDeliverEventArgs entrega)
    {
        await emAndamento.WaitAsync();
        try
        {
            if (parando)
            {
                // Chegou depois do pedido de desligar: fica sem ack e volta para a fila
                return;
            }

            await ProcessarAsync(canal, entrega);
        }
        catch (Exception erro) when (erro is AlreadyClosedException or OperationInterruptedException)
        {
            // A conexão caiu antes do ack: o RabbitMQ entrega de novo, e o ON CONFLICT segura a repetição
            log.LogInformation("Sem conexão para confirmar a mensagem {Tag}; ela será entregue de novo", entrega.DeliveryTag);
        }
        finally
        {
            emAndamento.Release();
        }
    }

    private async Task ProcessarAsync(IChannel canal, BasicDeliverEventArgs entrega)
    {
        var clique = CliqueRegistrado.DeBytes(entrega.Body.Span);
        if (clique is null || clique.Codigo.Length > Classificador.TamanhoDoCodigo)
        {
            // Mensagem que nunca vai dar certo: tentar de novo seria um laço infinito. Vai para a
            // "dead letter" (beacon.estatisticas.mortos), onde dá para olhar depois.
            log.LogWarning("Mensagem inválida descartada para {Fila} ({Bytes} bytes)", Topologia.FilaDosMortos, entrega.Body.Length);
            await canal.BasicRejectAsync(entrega.DeliveryTag, requeue: false);
            return;
        }

        try
        {
            // Sem o token de parar: a mensagem em andamento termina mesmo durante o desligamento
            await gravador.GravarAsync(clique, CancellationToken.None);
        }
        catch (PostgresException erro) when (erro.SqlState.StartsWith("22", StringComparison.Ordinal) || erro.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            // Classes 22 (dado inválido) e 23 (restrição violada): repetir daria o mesmo erro
            log.LogError(erro, "O banco recusou o clique {Id}; mensagem enviada para {Fila}", clique.Id, Topologia.FilaDosMortos);
            await canal.BasicRejectAsync(entrega.DeliveryTag, requeue: false);
            return;
        }
        catch (Exception erro) when (erro is NpgsqlException or TimeoutException)
        {
            // Banco fora do ar, rede, tempo esgotado: passa sozinho, então a mensagem volta para a fila
            await DevolverAsync(canal, entrega, erro);
            return;
        }
        catch (ObjectDisposedException erro)
        {
            // O serviço está desligando no meio da gravação (o banco já foi liberado): o clique é
            // bom, então volta para a fila em vez de ir para a dead letter
            await DevolverAsync(canal, entrega, erro);
            return;
        }
        catch (Exception erro)
        {
            // Erro inesperado (um bug): sem isto a mensagem ficaria sem ack, ocupando a fila até o
            // canal fechar. Na dead letter ela fica guardada para investigar e reprocessar.
            log.LogError(erro, "Erro inesperado no clique {Id}; mensagem enviada para {Fila}", clique.Id, Topologia.FilaDosMortos);
            await canal.BasicRejectAsync(entrega.DeliveryTag, requeue: false);
            return;
        }

        if (falhasDoBanco > 0)
        {
            log.LogInformation("Banco voltou depois de {Falhas} falha(s)", falhasDoBanco);
            falhasDoBanco = 0;
        }

        await canal.BasicAckAsync(entrega.DeliveryTag, multiple: false);
    }

    /// <summary>Banco fora do ar: espera um pouco e devolve a mensagem para a fila.</summary>
    private async Task DevolverAsync(IChannel canal, BasicDeliverEventArgs entrega, Exception erro)
    {
        falhasDoBanco++;
        // Um aviso completo na primeira falha; as seguintes só em nível Debug, para não inundar o log
        if (falhasDoBanco == 1)
        {
            log.LogWarning(erro, "Banco indisponível; as mensagens voltam para a fila até ele voltar");
        }
        else
        {
            log.LogDebug("Banco ainda indisponível ({Falhas} falhas): {Erro}", falhasDoBanco, erro.Message);
        }

        // Espera crescente (1, 2, 4... até 30 s) antes do nack: sem ela, a mesma mensagem voltaria na
        // hora e o serviço ficaria num laço quente. Enquanto espera, nenhuma outra mensagem é processada.
        var espera = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(falhasDoBanco - 1, 5)));
        try
        {
            await Task.Delay(espera < EsperaMaxima ? espera : EsperaMaxima, parar);
        }
        catch (OperationCanceledException)
        {
            // Desligando: devolve já
        }

        await canal.BasicNackAsync(entrega.DeliveryTag, multiple: false, requeue: true);
    }

    private static TimeSpan Dobrar(TimeSpan espera) => espera * 2 < EsperaMaxima ? espera * 2 : EsperaMaxima;
}
