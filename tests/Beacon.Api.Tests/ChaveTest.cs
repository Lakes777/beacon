using System.Net;
using System.Net.Http.Json;
using Beacon.Api.Contas;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Hosting;

namespace Beacon.Api.Tests;

/// <summary>A chave de integração: outro programa (o Pursuit) usando a API sem cookie e sem a senha.</summary>
public class ChaveTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private static string CodigoUnico() => "k-" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task ComAChaveCriaLinkELeAsEstatisticas()
    {
        var cliente = api.ClienteComChave();
        var codigo = CodigoUnico();

        var criado = await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://lakes777.github.io", codigo), Cancelar);
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        // A chave não abre sessão: nenhum cookie volta
        Assert.False(criado.Headers.Contains("Set-Cookie"));

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync($"/api/links/{codigo}/estatisticas", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/estatisticas", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task QuemEntraPelaChaveApareceComoIntegracao()
    {
        var sessao = await api.ClienteComChave().GetFromJsonAsync<SessaoResposta>("/api/sessao", Cancelar);

        Assert.Equal(ChaveDeIntegracao.Nome, sessao!.Usuario);
    }

    [Theory]
    [InlineData("chave-errada-mas-comprida-o-bastante-0000000")]
    [InlineData(ApiDeTeste.ChaveDeIntegracao + "x")]
    public async Task ChaveErradaDa401(string chave)
    {
        var resposta = await api.ClienteComChave(chave).GetAsync("/api/links", Cancelar);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Contains("Bearer", resposta.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task BearerVazioDa401()
    {
        var cliente = api.CreateClient();
        cliente.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");

        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task EsquemaEmMinusculasTambemVale()
    {
        var cliente = api.CreateClient();
        cliente.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer " + ApiDeTeste.ChaveDeIntegracao);

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task AChaveNaoEditaNemApaga()
    {
        var codigo = CodigoUnico();
        var criado = await api.ClienteLogado().PostAsJsonAsync("/api/links", new NovoLink("https://lakes777.github.io", codigo), Cancelar);
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var cliente = api.ClienteComChave();

        var editar = await cliente.PutAsJsonAsync($"/api/links/{codigo}", new EdicaoDeLink("https://outro.exemplo", false), Cancelar);
        var apagar = await cliente.DeleteAsync($"/api/links/{codigo}", Cancelar);

        Assert.Equal(HttpStatusCode.Forbidden, editar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apagar.StatusCode);
        // O link continua igual
        var link = await api.ClienteLogado().GetFromJsonAsync<LinkResposta>($"/api/links/{codigo}", Cancelar);
        Assert.Equal("https://lakes777.github.io/", link!.Destino);
        Assert.True(link.Ativo);
    }

    [Fact]
    public async Task ComOCookieDoPainelJuntoValeOPainel()
    {
        var cliente = api.ClienteLogado();
        cliente.DefaultRequestHeaders.Authorization = new("Bearer", ApiDeTeste.ChaveDeIntegracao);
        var codigo = CodigoUnico();
        Assert.Equal(HttpStatusCode.Created,
            (await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://lakes777.github.io", codigo), Cancelar)).StatusCode);

        // Quem tem o cookie pode apagar, mesmo mandando a chave junto
        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"/api/links/{codigo}", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task CookieValidoComChaveErradaContinuaLogado()
    {
        var cliente = api.ClienteLogado();
        cliente.DefaultRequestHeaders.Authorization = new("Bearer", "chave-errada-mas-comprida-o-bastante-0000000");

        var sessao = await cliente.GetFromJsonAsync<SessaoResposta>("/api/sessao", Cancelar);

        Assert.Equal(ApiDeTeste.Usuario, sessao!.Usuario);
    }

    [Fact]
    public async Task ChaveErradaNaoAtrapalhaAsRotasPublicas()
    {
        var cliente = api.ClienteComChave("chave-errada-mas-comprida-o-bastante-0000000");

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/saude", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/r/" + CodigoUnico(), Cancelar)).StatusCode);
    }

    [Fact]
    public async Task SemChaveConfiguradaOBearerNaoVale()
    {
        await using var semChave = api.WithWebHostBuilder(b => b.UseSetting("Integracao:Chave", ""));
        var cliente = semChave.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new("Bearer", ApiDeTeste.ChaveDeIntegracao);

        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/links", Cancelar)).StatusCode);
        // O cookie continua valendo nessa API
        var logado = await ApiDeTeste.Entrar(semChave);
        Assert.Equal(HttpStatusCode.OK, (await logado.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public void ChaveCurtaImpedeAApiDeSubir()
    {
        using var comChaveCurta = api.WithWebHostBuilder(b => b.UseSetting("Integracao:Chave", "curta"));

        var erro = Assert.Throws<InvalidOperationException>(() => comChaveCurta.CreateClient());
        Assert.Contains("openssl rand -hex 32", erro.Message);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abcd", false)]
    [InlineData("", "abc", false)]
    public void ComparaAChave(string recebida, string esperada, bool iguais) =>
        Assert.Equal(iguais, ChaveDeIntegracao.Iguais(recebida, esperada));
}
