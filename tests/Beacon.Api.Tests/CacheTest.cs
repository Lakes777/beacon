using System.Net;
using System.Net.Http.Json;
using Beacon.Api.Cache;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Beacon.Api.Tests;

/// <summary>O Redis na frente do redirecionamento: acerto, invalidação e Redis fora do ar.</summary>
public class CacheTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private readonly HttpClient cliente = api.ClienteLogado(seguirRedirecionamentos: false);

    private static string CodigoUnico() => "c-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<string> Criar(string destino)
    {
        var codigo = CodigoUnico();
        var resposta = await cliente.PostAsJsonAsync("/api/links", new NovoLink(destino, codigo), Cancelar);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return codigo;
    }

    private async Task<(HttpStatusCode Status, string? Destino, string Cache)> Clicar(string codigo)
    {
        var resposta = await cliente.GetAsync($"/r/{codigo}", Cancelar);
        return (resposta.StatusCode, resposta.Headers.Location?.OriginalString,
            resposta.Headers.GetValues("X-Beacon-Cache").Single());
    }

    private IDatabase Redis => api.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    [Fact]
    public async Task OPrimeiroCliqueVaiAoBancoEOSegundoNao()
    {
        var codigo = await Criar("https://exemplo.com/");

        Assert.Equal((HttpStatusCode.Redirect, "https://exemplo.com/", "MISS"), await Clicar(codigo));
        Assert.Equal((HttpStatusCode.Redirect, "https://exemplo.com/", "HIT"), await Clicar(codigo));
        // A cópia tem prazo: some sozinha mesmo se uma invalidação falhar
        var prazo = await Redis.KeyTimeToLiveAsync(CacheDeLinks.Chave(codigo));
        Assert.InRange(prazo!.Value, CacheDeLinks.Validade - TimeSpan.FromMinutes(1), CacheDeLinks.Validade);
    }

    [Fact]
    public async Task EditarODestinoValeNoProximoClique()
    {
        var codigo = await Criar("https://exemplo.com/antes");
        await Clicar(codigo);   // agora está no cache

        await cliente.PutAsJsonAsync($"/api/links/{codigo}", new EdicaoDeLink("https://exemplo.com/depois", null), Cancelar);

        Assert.Equal((HttpStatusCode.Redirect, "https://exemplo.com/depois", "MISS"), await Clicar(codigo));
    }

    [Fact]
    public async Task DesativarEApagarValemNoProximoClique()
    {
        var desativado = await Criar("https://exemplo.com/");
        var apagado = await Criar("https://exemplo.com/");
        await Clicar(desativado);
        await Clicar(apagado);

        await cliente.PutAsJsonAsync($"/api/links/{desativado}", new EdicaoDeLink("https://exemplo.com/", false), Cancelar);
        await cliente.DeleteAsync($"/api/links/{apagado}", Cancelar);

        Assert.Equal(HttpStatusCode.NotFound, (await Clicar(desativado)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Clicar(apagado)).Status);
    }

    [Fact]
    public async Task NaoExisteFicaGuardadoMasUmLinkNovoFuncionaNaHora()
    {
        var codigo = CodigoUnico();
        Assert.Equal((HttpStatusCode.NotFound, null, "MISS"), await Clicar(codigo));
        Assert.Equal((HttpStatusCode.NotFound, null, "HIT"), await Clicar(codigo));   // não foi ao banco

        await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com/novo", codigo), Cancelar);

        Assert.Equal((HttpStatusCode.Redirect, "https://exemplo.com/novo", "MISS"), await Clicar(codigo));
    }

    [Fact]
    public async Task MaiusculasEMinusculasUsamAMesmaCopia()
    {
        var codigo = await Criar("https://exemplo.com/");

        Assert.Equal("MISS", (await Clicar(codigo.ToUpperInvariant())).Cache);
        Assert.Equal("HIT", (await Clicar(codigo)).Cache);
    }

    [Fact]
    public async Task TextoQueNaoPodeSerCodigoNemChegaAoCache()
    {
        var lixo = new string('x', 500);

        var resposta = await cliente.GetAsync($"/r/{lixo}", Cancelar);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("X-Beacon-Cache"));
        Assert.False(await Redis.KeyExistsAsync(CacheDeLinks.Chave(lixo)));
    }

    [Fact]
    public async Task ComORedisForaDoArOsLinksContinuamFuncionando()
    {
        var codigo = await Criar("https://exemplo.com/");
        // Mesma API e mesmo banco, mas o Redis aponta para uma porta onde não há nada
        await using var semRedis = api.WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1"));
        var outroCliente = await ApiDeTeste.Entrar(semRedis, seguirRedirecionamentos: false);
        await outroCliente.GetAsync($"/r/{codigo}", Cancelar);   // o primeiro pedido abre a conexão

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var ida = await outroCliente.GetAsync($"/r/{codigo}", Cancelar);
        var tempo = relogio.Elapsed;
        var edicao = await outroCliente.PutAsJsonAsync($"/api/links/{codigo}",
            new EdicaoDeLink("https://exemplo.com/outro", null), Cancelar);
        var saude = await outroCliente.GetAsync("/saude", Cancelar);

        Assert.Equal(HttpStatusCode.Redirect, ida.StatusCode);
        Assert.Equal("https://exemplo.com/", ida.Headers.Location!.OriginalString);
        Assert.Equal("MISS", ida.Headers.GetValues("X-Beacon-Cache").Single());
        // Sem esperar o Redis: falha na hora e vai ao banco
        Assert.True(tempo < TimeSpan.FromMilliseconds(300), $"o clique levou {tempo.TotalMilliseconds:F0} ms");
        Assert.Equal(HttpStatusCode.OK, edicao.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saude.StatusCode);
        Assert.Equal("Degraded", await saude.Content.ReadAsStringAsync(Cancelar));
    }
}
