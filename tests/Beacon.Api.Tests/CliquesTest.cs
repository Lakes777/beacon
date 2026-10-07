using System.Net;
using System.Net.Http.Json;
using Beacon.Api.Links;
using Beacon.Api.Mensageria;
using Beacon.Contratos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Beacon.Api.Tests;

/// <summary>Cada redirecionamento vira uma mensagem no RabbitMQ, sem depender dele para funcionar.</summary>
public class CliquesTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private readonly HttpClient cliente = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string CodigoUnico() => "q-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<string> Criar()
    {
        var codigo = CodigoUnico();
        var resposta = await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com/", codigo), Cancelar);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return codigo;
    }

    [Fact]
    public async Task UmCliqueChegaNaFilaComOsCabecalhos()
    {
        var codigo = await Criar();
        var pedido = new HttpRequestMessage(HttpMethod.Get, $"/r/{codigo.ToUpperInvariant()}");
        pedido.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0");
        pedido.Headers.Referrer = new Uri("https://www.linkedin.com/feed/");
        var antes = DateTimeOffset.UtcNow;

        var resposta = await cliente.SendAsync(pedido, Cancelar);

        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        var (mensagem, clique) = await Procurar(codigo);
        Assert.Equal(codigo, clique.Codigo);    // já normalizado
        Assert.Equal("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", clique.UserAgent);
        Assert.Equal("https://www.linkedin.com/feed/", clique.Referer);
        Assert.InRange(clique.Momento, antes, DateTimeOffset.UtcNow);
        Assert.Equal(7, clique.Id.Version);     // Guid.CreateVersion7: ordenado pelo tempo
        Assert.Equal(clique.Id.ToString(), mensagem.BasicProperties.MessageId);
        Assert.Equal("application/json", mensagem.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, mensagem.BasicProperties.DeliveryMode);
    }

    [Fact]
    public async Task LinkQueNaoExisteNaoGeraClique()
    {
        var codigo = CodigoUnico();
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/r/{codigo}", Cancelar)).StatusCode);
        // Um clique que dá certo depois serve de marco: se o 404 tivesse publicado, viria antes dele
        await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com/", codigo), Cancelar);
        await cliente.GetAsync($"/r/{codigo}", Cancelar);

        var (_, clique) = await Procurar(codigo);

        Assert.Null(clique.UserAgent);
        Assert.Null(await Procurar(codigo, TimeSpan.FromMilliseconds(500), falharSeNaoAchar: false));
    }

    [Fact]
    public async Task ComORabbitMQForaDoArOsLinksContinuamFuncionando()
    {
        var codigo = await Criar();
        // Mesma API e mesmo banco, mas o RabbitMQ aponta para uma porta onde não há nada
        await using var semRabbitMQ = api.WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:RabbitMQ", "amqp://guest:guest@127.0.0.1:1"));
        var outroCliente = semRabbitMQ.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await outroCliente.GetAsync($"/r/{codigo}", Cancelar);   // o primeiro pedido sobe a API

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var ida = await outroCliente.GetAsync($"/r/{codigo}", Cancelar);
        var tempo = relogio.Elapsed;
        var saude = await outroCliente.GetAsync("/saude", Cancelar);

        Assert.Equal(HttpStatusCode.Redirect, ida.StatusCode);
        Assert.True(tempo < TimeSpan.FromMilliseconds(300), $"o clique levou {tempo.TotalMilliseconds:F0} ms");
        Assert.Equal(HttpStatusCode.OK, saude.StatusCode);
        Assert.Equal("Degraded", await saude.Content.ReadAsStringAsync(Cancelar));
        // Os dois cliques esperam na memória até o RabbitMQ voltar
        Assert.Equal(2, semRabbitMQ.Services.GetRequiredService<FilaDeCliques>().Pendentes);
    }

    [Fact]
    public void FilaCheiaDescartaOsNovos()
    {
        var fila = new FilaDeCliques(NullLogger<FilaDeCliques>.Instance);
        for (var i = 0; i < FilaDeCliques.Capacidade; i++)
        {
            Assert.True(fila.Entregar(NovoClique()));
        }

        Assert.False(fila.Entregar(NovoClique()));
        Assert.Equal(FilaDeCliques.Capacidade, fila.Pendentes);
        // Saiu um, entra um
        Assert.True(fila.Leitor.TryRead(out _));
        Assert.True(fila.Entregar(NovoClique()));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("curl/8.5.0", "curl/8.5.0")]
    public void CabecalhoVazioViraNull(string valor, string? esperado) => Assert.Equal(esperado, LinkRotas.Cabecalho(valor));

    [Fact]
    public void CabecalhoGiganteECortado() =>
        Assert.Equal(LinkRotas.TamanhoDoCabecalho, LinkRotas.Cabecalho(new string('a', 50_000))!.Length);

    private static CliqueRegistrado NovoClique() =>
        new(Guid.CreateVersion7(), "abc", DateTimeOffset.UtcNow, null, null);

    private async Task<(BasicGetResult Mensagem, CliqueRegistrado Clique)> Procurar(string codigo) =>
        (await Procurar(codigo, TimeSpan.FromSeconds(10), falharSeNaoAchar: true))!.Value;

    /// <summary>
    /// Lê a fila até achar o clique deste código. A fila é de todos os testes (cada redirecionamento
    /// publica), então as mensagens dos outros são lidas sem confirmar (ack): ao fechar o canal, o
    /// RabbitMQ as devolve para a fila.
    /// </summary>
    private async Task<(BasicGetResult Mensagem, CliqueRegistrado Clique)?> Procurar(
        string codigo, TimeSpan espera, bool falharSeNaoAchar)
    {
        var fabrica = new ConnectionFactory { Uri = new Uri(api.ConexaoDoRabbitMQ) };
        await using var conexao = await fabrica.CreateConnectionAsync(Cancelar);
        await using var canal = await conexao.CreateChannelAsync(cancellationToken: Cancelar);
        var limite = DateTime.UtcNow + espera;
        while (DateTime.UtcNow < limite)
        {
            var mensagem = await canal.BasicGetAsync(Topologia.Fila, autoAck: false, Cancelar);
            if (mensagem is null)
            {
                // Fila vazia (para este canal): o publicador ainda não mandou
                await Task.Delay(50, Cancelar);
                continue;
            }
            var clique = CliqueRegistrado.DeBytes(mensagem.Body.Span);
            if (clique?.Codigo == codigo)
            {
                await canal.BasicAckAsync(mensagem.DeliveryTag, multiple: false, Cancelar);
                return (mensagem, clique);
            }
        }
        if (falharSeNaoAchar)
        {
            Assert.Fail($"O clique de {codigo} não chegou na fila {Topologia.Fila}.");
        }
        return null;
    }
}
