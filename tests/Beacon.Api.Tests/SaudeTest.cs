using System.Net;
using Beacon.Api.Painel;

namespace Beacon.Api.Tests;

public class SaudeTest(ApiDeTeste api)
{
    [Fact]
    public async Task SaudeRespondeQuandoOBancoEstaNoAr()
    {
        var resposta = await api.CreateClient().GetAsync("/saude", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("Healthy", await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InicioEhOPainelComCabecalhosDeSeguranca()
    {
        // Sem login: a página é pública (os números vêm da API, que pede login)
        var resposta = await api.CreateClient().GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("text/html", resposta.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Beacon</title>", await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CabecalhosDoPainel.Csp, resposta.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", resposta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resposta.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-cache", resposta.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/js/painel.js", "text/javascript")]
    [InlineData("/css/beacon.css", "text/css")]
    [InlineData("/img/estrela.svg", "image/svg+xml")]
    [InlineData("/fontes/bricolage-grotesque.woff2", "font/woff2")]
    public async Task ArquivosDoPainelSaoPublicos(string caminho, string tipo)
    {
        var resposta = await api.CreateClient().GetAsync(caminho, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(tipo, resposta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", resposta.Headers.CacheControl?.ToString());
        Assert.NotNull(resposta.Headers.ETag);   // o navegador pergunta "mudou?" e recebe 304 se não
    }
}
