using System.Threading.Channels;
using Beacon.Contratos;

namespace Beacon.Api.Mensageria;

/// <summary>
/// Fila em memória entre o redirecionamento e o RabbitMQ. O clique só deixa a mensagem aqui
/// (instantâneo) e já redireciona; quem publica no RabbitMQ é o PublicadorDeCliques, em segundo plano.
/// Assim um RabbitMQ lento ou fora do ar nunca atrasa nem quebra o clique.
/// </summary>
public sealed class FilaDeCliques(ILogger<FilaDeCliques> log)
{
    /// <summary>
    /// Limite para a memória não crescer sem fim com o RabbitMQ fora do ar por muito tempo.
    /// Cheia, os cliques novos são descartados: perder estatística é melhor que derrubar a API.
    /// </summary>
    public const int Capacidade = 10_000;

    // Wait + TryWrite: cheia, o TryWrite devolve false na hora (sem esperar), e a API fica sabendo
    // que perdeu o clique. Com DropWrite, ele devolveria true e o descarte seria silencioso.
    private readonly Channel<CliqueRegistrado> canal = Channel.CreateBounded<CliqueRegistrado>(
        new BoundedChannelOptions(Capacidade)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    // Cliques descartados desde que a fila encheu (0 = não está descartando)
    private long descartados;

    public ChannelReader<CliqueRegistrado> Leitor => canal.Reader;

    /// <summary>Quantos cliques esperam para ir ao RabbitMQ.</summary>
    public int Pendentes => canal.Reader.Count;

    /// <returns>false se a fila estava cheia e o clique foi descartado.</returns>
    public bool Entregar(CliqueRegistrado clique)
    {
        if (canal.Writer.TryWrite(clique))
        {
            // Lê antes de trocar: no caso comum (nada descartado) não escreve nada na memória compartilhada
            if (Interlocked.Read(ref descartados) > 0 && Interlocked.Exchange(ref descartados, 0) is var perdidos and > 0)
            {
                log.LogWarning("A fila de cliques voltou a aceitar; {Perdidos} cliques foram descartados", perdidos);
            }
            return true;
        }
        // Um aviso quando começa a descartar e um resumo quando para (e não um por clique)
        if (Interlocked.Increment(ref descartados) == 1)
        {
            log.LogWarning("A fila de cliques está cheia ({Capacidade}); descartando os novos até o RabbitMQ voltar",
                Capacidade);
        }
        return false;
    }
}
