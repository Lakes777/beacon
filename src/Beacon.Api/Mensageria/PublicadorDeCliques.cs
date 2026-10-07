using Beacon.Contratos;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Beacon.Api.Mensageria;

/// <summary>
/// Tira os cliques da FilaDeCliques e publica no RabbitMQ, um por vez, esperando a confirmação do
/// broker ("publisher confirms") antes de pegar o próximo: só larga a mensagem quando o RabbitMQ
/// garantiu que ela está guardada. Se o RabbitMQ estiver fora, segura a mensagem em mãos e tenta de
/// novo com espera crescente; os outros cliques esperam na fila em memória.
/// </summary>
public sealed class PublicadorDeCliques(
    ConnectionFactory fabrica, FilaDeCliques fila, ILogger<PublicadorDeCliques> log) : BackgroundService
{
    private static readonly TimeSpan PrimeiraEspera = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaiorEspera = TimeSpan.FromSeconds(30);

    /// <summary>Quanto esperar a confirmação: um RabbitMQ travado não pode segurar a fila para sempre.</summary>
    private static readonly TimeSpan LimiteDaConfirmacao = TimeSpan.FromSeconds(10);

    // volatile: o /saude lê estes campos em outra thread
    private volatile IConnection? conexao;
    private volatile IChannel? canal;

    // -1 = ainda não sabe, 0 = fora do ar, 1 = no ar. Só a mudança vai para o log.
    private int estado = -1;

    /// <summary>Para o /saude. Durante a recuperação automática da conexão, IsOpen fica false.</summary>
    public bool Conectado => conexao is { IsOpen: true } && canal is { IsOpen: true };

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        CliqueRegistrado? emMaos = null;
        var espera = PrimeiraEspera;
        try
        {
            while (!parar.IsCancellationRequested)
            {
                try
                {
                    // Conecta antes de ler a fila: assim o /saude sabe do RabbitMQ mesmo sem cliques
                    canal ??= await AbrirCanal(parar);
                    emMaos ??= await fila.Leitor.ReadAsync(parar);
                    await Publicar(canal, emMaos, parar);
                    emMaos = null;
                    espera = PrimeiraEspera;
                    MudarEstado(noAr: true);
                }
                catch (OperationCanceledException) when (parar.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception erro)
                {
                    // Debug: com o RabbitMQ fora, seria uma linha a cada tentativa. A queda vai uma vez só.
                    log.LogDebug(erro, "Falha ao publicar no RabbitMQ; nova tentativa em {Espera}", espera);
                    MudarEstado(noAr: false, erro.Message);
                    await DescartarCanalSeQuebrou(erro);
                    await Task.Delay(espera, parar);
                    espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, MaiorEspera.Ticks));
                }
            }
        }
        catch (OperationCanceledException) when (parar.IsCancellationRequested)
        {
            // A API está parando durante uma espera
        }
        finally
        {
            await EsvaziarAoDesligar(emMaos);
            emMaos = null;
            // O que sobrar na fila em memória se perde ao desligar (ver o README, "Estatísticas e fila")
            if (fila.Pendentes + (emMaos is null ? 0 : 1) is var perdidos and > 0)
            {
                log.LogWarning("A API parou com {Perdidos} cliques ainda não publicados", perdidos);
            }
            await Fechar();
        }
    }

    private async Task<IChannel> AbrirCanal(CancellationToken parar)
    {
        // A primeira conexão é nossa; depois de aberta, a biblioteca reconecta sozinha
        // (AutomaticRecoveryEnabled). Se ela nunca abriu, a recuperação automática não vale.
        if (conexao is null)
        {
            conexao = await fabrica.CreateConnectionAsync("beacon-api", parar);
            conexao.ConnectionShutdownAsync += AoCair;
            conexao.RecoverySucceededAsync += AoVoltar;
        }
        // Confirmações com acompanhamento: o BasicPublishAsync só termina quando o broker confirma,
        // e lança PublishException se ele recusar (nack) ou devolver a mensagem (sem fila).
        var novo = await conexao.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            parar);
        try
        {
            await Topologia.DeclararAsync(novo, parar);
        }
        catch
        {
            // Sem isso, cada tentativa que falha deixaria um canal órfão aberto na conexão
            await FecharEmSilencio(novo);
            throw;
        }
        return novo;
    }

    /// <summary>
    /// Ao desligar (ex.: deploy) com o RabbitMQ no ar, publica o que ainda está na memória, com um
    /// prazo curto e próprio (o token "parar" já foi cancelado nessa hora).
    /// </summary>
    private async Task EsvaziarAoDesligar(CliqueRegistrado? emMaos)
    {
        if (canal is not { IsOpen: true } aberto)
        {
            return;
        }
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (emMaos is not null)
            {
                await Publicar(aberto, emMaos, prazo.Token);
            }
            while (fila.Leitor.TryRead(out var clique))
            {
                await Publicar(aberto, clique, prazo.Token);
            }
        }
        catch (Exception erro)
        {
            log.LogDebug(erro, "Não deu tempo de publicar todos os cliques ao desligar");
        }
    }

    private async Task Publicar(IChannel canalAberto, CliqueRegistrado clique, CancellationToken parar)
    {
        var propriedades = new BasicProperties
        {
            // Persistente + fila durable: sobrevive a um reinício do RabbitMQ
            Persistent = true,
            ContentType = "application/json",
            // O consumidor não precisa abrir o JSON para saber de qual clique se trata
            MessageId = clique.Id.ToString(),
        };
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(parar);
        limite.CancelAfter(LimiteDaConfirmacao);
        // mandatory: sem fila ligada à exchange, o RabbitMQ devolve a mensagem em vez de jogá-la fora
        await canalAberto.BasicPublishAsync(Topologia.Exchange, Topologia.ChaveDoClique, mandatory: true,
            propriedades, clique.ParaBytes(), limite.Token);
    }

    /// <summary>
    /// Com a conexão caída, o canal volta junto com ela (recuperação automática): basta tentar de novo.
    /// Mas se só o canal fechou (erro de protocolo) ou a mensagem voltou sem fila, ele não volta
    /// sozinho: abre outro, que declara a topologia de novo.
    /// </summary>
    private async Task DescartarCanalSeQuebrou(Exception erro)
    {
        var soOCanal = canal is { IsOpen: false } && conexao is { IsOpen: true };
        if (canal is not null && (soOCanal || erro is PublishException { IsReturn: true }))
        {
            await FecharEmSilencio(canal);
            canal = null;
        }
    }

    private Task AoCair(object remetente, ShutdownEventArgs motivo)
    {
        // Fechamento pedido por nós (a API parando) não é queda
        if (motivo.Initiator != ShutdownInitiator.Application)
        {
            MudarEstado(noAr: false, motivo.ReplyText);
        }
        return Task.CompletedTask;
    }

    private Task AoVoltar(object remetente, AsyncEventArgs args)
    {
        MudarEstado(noAr: true);
        return Task.CompletedTask;
    }

    /// <summary>Um aviso quando o RabbitMQ cai e outro quando volta (e não um a cada tentativa).</summary>
    private void MudarEstado(bool noAr, string? motivo = null)
    {
        var anterior = Interlocked.Exchange(ref estado, noAr ? 1 : 0);
        if (anterior == (noAr ? 1 : 0))
        {
            return;
        }
        if (noAr)
        {
            log.LogInformation("Conexão com o RabbitMQ no ar; publicando os cliques");
        }
        else
        {
            log.LogWarning("RabbitMQ indisponível ({Motivo}); os cliques esperam na memória (até {Capacidade})",
                motivo, FilaDeCliques.Capacidade);
        }
    }

    private async Task Fechar()
    {
        if (canal is not null)
        {
            await FecharEmSilencio(canal);
        }
        if (conexao is not null)
        {
            conexao.ConnectionShutdownAsync -= AoCair;
            conexao.RecoverySucceededAsync -= AoVoltar;
            try
            {
                await conexao.CloseAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception erro) when (erro is not OutOfMemoryException)
            {
                log.LogDebug(erro, "Erro ao fechar a conexão com o RabbitMQ");
            }
            conexao.Dispose();
        }
    }

    private async Task FecharEmSilencio(IChannel aFechar)
    {
        try
        {
            await aFechar.CloseAsync();
        }
        catch (Exception erro) when (erro is not OutOfMemoryException)
        {
            // Já estava fechado ou a conexão caiu: não há o que fazer
            log.LogDebug(erro, "Erro ao fechar o canal do RabbitMQ");
        }
        aFechar.Dispose();
    }
}
