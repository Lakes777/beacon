using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Beacon.Api.Tests;

/// <summary>As rotas de /api/links e o redirecionamento, de ponta a ponta (HTTP + Postgres).</summary>
public class LinksTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    // Sem seguir redirecionamentos: o teste quer ver o 302 e o Location
    private readonly HttpClient cliente = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string CodigoUnico() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<LinkResposta> Criar(string destino, string? codigo = null)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/links", new NovoLink(destino, codigo), Cancelar);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var link = (await resposta.Content.ReadFromJsonAsync<LinkResposta>(Cancelar))!;
        Assert.Equal($"/api/links/{link.Codigo}", resposta.Headers.Location!.ToString());
        return link;
    }

    private static async Task<JsonElement> Erros(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<JsonElement>(Cancelar)).GetProperty("errors");

    [Fact]
    public async Task CriaComCodigoEscolhidoERedireciona()
    {
        var codigo = CodigoUnico();
        var link = await Criar("https://lakes777.github.io", codigo.ToUpperInvariant());

        Assert.Equal(codigo, link.Codigo);    // guardado em minúsculas
        Assert.True(link.Ativo);
        Assert.EndsWith($"/r/{codigo}", link.UrlCurta);

        var ida = await cliente.GetAsync($"/r/{codigo}", Cancelar);
        Assert.Equal(HttpStatusCode.Redirect, ida.StatusCode);
        Assert.Equal("https://lakes777.github.io/", ida.Headers.Location!.ToString());
        // Maiúsculas no endereço também funcionam
        Assert.Equal(HttpStatusCode.Redirect, (await cliente.GetAsync($"/r/{codigo.ToUpperInvariant()}", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task SemCodigoGeraUmAleatorio()
    {
        var link = await Criar("https://exemplo.com");

        Assert.Equal(Codigos.TamanhoGerado, link.Codigo.Length);
        Assert.Null(Codigos.Problema(link.Codigo));
    }

    [Fact]
    public async Task CodigoEmUsoDa409()
    {
        var codigo = CodigoUnico();
        await Criar("https://exemplo.com/1", codigo);

        var resposta = await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com/2", codigo), Cancelar);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Contains(codigo, await resposta.Content.ReadAsStringAsync(Cancelar));
    }

    [Fact]
    public async Task DoisPedidosAoMesmoTempoComOMesmoCodigo()
    {
        var codigo = CodigoUnico();
        var pedidos = Enumerable.Range(0, 5).Select(i =>
            cliente.PostAsJsonAsync("/api/links", new NovoLink($"https://exemplo.com/{i}", codigo), Cancelar));

        var respostas = await Task.WhenAll(pedidos);

        Assert.Single(respostas, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task DadosInvalidosDao400ComOsCampos()
    {
        var resposta = await cliente.PostAsJsonAsync("/api/links", new NovoLink("javascript:alert(1)", "-x"), Cancelar);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var erros = await Erros(resposta);
        Assert.True(erros.TryGetProperty("destino", out _));
        Assert.True(erros.TryGetProperty("codigo", out _));
    }

    [Fact]
    public async Task ListaDoMaisNovoAoMaisAntigo()
    {
        var antigo = await Criar("https://exemplo.com/antigo");
        var novo = await Criar("https://exemplo.com/novo");

        var lista = await cliente.GetFromJsonAsync<List<LinkResposta>>("/api/links", Cancelar);
        var codigos = lista!.Select(l => l.Codigo).ToList();

        Assert.True(codigos.IndexOf(novo.Codigo) < codigos.IndexOf(antigo.Codigo));
    }

    [Fact]
    public async Task EditarTrocaODestinoEMantemOAtivoSeOmitido()
    {
        var link = await Criar("https://exemplo.com/antes");

        var resposta = await cliente.PutAsJsonAsync($"/api/links/{link.Codigo}", new { destino = "https://exemplo.com/depois" }, Cancelar);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var ida = await cliente.GetAsync($"/r/{link.Codigo}", Cancelar);
        Assert.Equal("https://exemplo.com/depois", ida.Headers.Location!.ToString());
    }

    [Fact]
    public async Task LinkDesativadoNaoRedirecionaMasContinuaNaLista()
    {
        var link = await Criar("https://exemplo.com");

        await cliente.PutAsJsonAsync($"/api/links/{link.Codigo}", new EdicaoDeLink("https://exemplo.com", false), Cancelar);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/r/{link.Codigo}", Cancelar)).StatusCode);
        var salvo = await cliente.GetFromJsonAsync<LinkResposta>($"/api/links/{link.Codigo}", Cancelar);
        Assert.False(salvo!.Ativo);
    }

    [Fact]
    public async Task EditarComDestinoInvalidoDa400()
    {
        var link = await Criar("https://exemplo.com");

        var resposta = await cliente.PutAsJsonAsync($"/api/links/{link.Codigo}", new EdicaoDeLink("ftp://x", null), Cancelar);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task ApagarTiraOLinkDoAr()
    {
        var link = await Criar("https://exemplo.com");

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"/api/links/{link.Codigo}", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/r/{link.Codigo}", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.DeleteAsync($"/api/links/{link.Codigo}", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task CodigoQueNaoExisteDa404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/r/nao-existe-mesmo", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/links/nao-existe-mesmo", Cancelar)).StatusCode);
        var edicao = await cliente.PutAsJsonAsync("/api/links/nao-existe-mesmo", new EdicaoDeLink("https://x.com", null), Cancelar);
        Assert.Equal(HttpStatusCode.NotFound, edicao.StatusCode);
    }

    [Theory]
    [InlineData("")]                        // corpo vazio
    [InlineData("{\"destino\":")]           // JSON cortado
    public async Task PedidoMalformadoDa400EmProblemDetails(string corpo)
    {
        var resposta = await cliente.PostAsync("/api/links",
            new StringContent(corpo, System.Text.Encoding.UTF8, "application/json"), Cancelar);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DestinoComAcentoRedirecionaCodificado()
    {
        var link = await Criar("https://pt.wikipedia.org/wiki/Programação");

        var ida = await cliente.GetAsync($"/r/{link.Codigo}", Cancelar);

        Assert.Equal("https://pt.wikipedia.org/wiki/Programa%C3%A7%C3%A3o", ida.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task DocumentacaoDaApi()
    {
        var openapi = await cliente.GetStringAsync("/openapi/v1.json", Cancelar);
        Assert.Contains("/api/links", openapi);
        // /docs leva a /docs/ (a página do Scalar); este cliente segue o redirecionamento
        var docs = await api.CreateClient().GetAsync("/docs", Cancelar);
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
    }
}
