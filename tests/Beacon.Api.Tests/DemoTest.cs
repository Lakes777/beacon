using System.Net;
using System.Net.Http.Json;
using Beacon.Api.Banco;
using Beacon.Api.Cache;
using Beacon.Api.Contas;
using Beacon.Api.Demo;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Beacon.Api.Tests;

/// <summary>
/// Uma API com Demo:Ativo ligado e banco próprio (outro database no mesmo Postgres): a semeadura
/// apaga todos os links, e no banco dos outros testes, que rodam em paralelo, ela os quebraria.
/// </summary>
public class DemoDeTeste(ApiDeTeste api) : IAsyncLifetime
{
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var banco = new NpgsqlConnectionStringBuilder(api.ConexaoDoBanco) { Database = "beacon_demo" }.ConnectionString;
        Fabrica = api.WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Banco", banco)
            .UseSetting(ModoDemo.Configuracao, "true"));
        // Sobe a API (migra o banco novo e, vazio, semeia) e cria a conta real nele
        await using var escopo = Fabrica.Services.CreateAsyncScope();
        await RegrasDeConta.DefinirSenha(escopo.ServiceProvider.GetRequiredService<BeaconContexto>(),
            escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(), ApiDeTeste.Usuario, ApiDeTeste.Senha);
    }

    public async ValueTask DisposeAsync() => await Fabrica.DisposeAsync();
}

/// <summary>O modo demonstração: o visitante entra sem senha e só lê; a semeadura dos dados de exemplo.</summary>
public class DemoTest(ApiDeTeste api, DemoDeTeste demo) : IClassFixture<DemoDeTeste>
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private async Task<HttpClient> Visitante()
    {
        // Guarda o cookie e o manda de volta, como um navegador
        var cliente = demo.Fabrica.CreateClient();
        var resposta = await cliente.PostAsync("/api/sessao/visitante", null, Cancelar);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return cliente;
    }

    private async Task<string> UmCodigoSemeado()
    {
        await using var escopo = demo.Fabrica.Services.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        return await banco.Links.Where(l => l.Codigo == "portfolio-linkedin").Select(l => l.Codigo).SingleOrDefaultAsync(Cancelar)
            ?? throw new InvalidOperationException("A demonstração deveria ter subido semeada.");
    }

    private async Task<(int Links, int Cliques)> Semear()
    {
        await using var escopo = demo.Fabrica.Services.CreateAsyncScope();
        return await SemeaduraDaDemo.Semear(escopo.ServiceProvider.GetRequiredService<BeaconContexto>(),
            escopo.ServiceProvider.GetRequiredService<CacheDeLinks>(), DateTimeOffset.UtcNow, Cancelar);
    }

    [Fact]
    public async Task ApiDemoDizSeEstaLigada()
    {
        Assert.True((await demo.Fabrica.CreateClient().GetFromJsonAsync<DemoResposta>("/api/demo", Cancelar))!.Demo);
        Assert.False((await api.CreateClient().GetFromJsonAsync<DemoResposta>("/api/demo", Cancelar))!.Demo);
    }

    [Fact]
    public async Task VisitanteEntraEApareceComoVisitante()
    {
        var cliente = await Visitante();

        var sessao = await cliente.GetFromJsonAsync<SessaoResposta>("/api/sessao", Cancelar);

        Assert.Equal(new SessaoResposta(ModoDemo.Visitante, Visitante: true), sessao);
    }

    [Fact]
    public async Task ComADemoDesligadaARotaDoVisitanteDa404()
    {
        var resposta = await api.CreateClient().PostAsync("/api/sessao/visitante", null, Cancelar);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task CookieDeVisitanteNaoValeComADemoDesligada()
    {
        var entrada = await demo.Fabrica.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .PostAsync("/api/sessao/visitante", null, Cancelar);
        var cookie = entrada.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var semDemo = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        semDemo.DefaultRequestHeaders.Add("Cookie", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, (await semDemo.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task VisitanteLeTudo()
    {
        var cliente = await Visitante();
        var codigo = await UmCodigoSemeado();

        var links = await cliente.GetFromJsonAsync<List<LinkResposta>>("/api/links", Cancelar);
        Assert.Contains(links!, l => l.Codigo == codigo);
        foreach (var rota in new[]
        {
            $"/api/links/{codigo}", $"/api/links/{codigo}/qr", $"/api/links/{codigo}/estatisticas?dias=30",
            "/api/estatisticas?dias=90",
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(rota, Cancelar)).StatusCode);
        }
    }

    [Fact]
    public async Task VisitanteNaoCriaNaoEditaNemApaga()
    {
        var cliente = await Visitante();
        var codigo = await UmCodigoSemeado();

        var respostas = new[]
        {
            await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://golpe.exemplo", "banco-falso"), Cancelar),
            await cliente.PutAsJsonAsync($"/api/links/{codigo}", new EdicaoDeLink("https://golpe.exemplo", false), Cancelar),
            await cliente.DeleteAsync($"/api/links/{codigo}", Cancelar),
        };

        foreach (var resposta in respostas)
        {
            Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
            Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
            var problema = await resposta.Content.ReadFromJsonAsync<ProblemDetails>(Cancelar);
            Assert.Equal(ModoDemo.Proibido, problema!.Detail);
        }
        // Nada mudou
        var link = await cliente.GetFromJsonAsync<LinkResposta>($"/api/links/{codigo}", Cancelar);
        Assert.Equal("https://andrelagos.dev/", link!.Destino);
        Assert.True(link.Ativo);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/links/banco-falso", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task VisitanteSai()
    {
        var cliente = await Visitante();

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync("/api/sessao", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task ContaRealContinuaFazendoTudoNaDemo()
    {
        var cliente = await ApiDeTeste.Entrar(demo.Fabrica);
        var codigo = "real-" + Guid.NewGuid().ToString("N")[..8];

        var sessao = await cliente.GetFromJsonAsync<SessaoResposta>("/api/sessao", Cancelar);
        Assert.False(sessao!.Visitante);
        Assert.Equal(HttpStatusCode.Created,
            (await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://lakes777.github.io", codigo), Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await cliente.PutAsJsonAsync($"/api/links/{codigo}", new EdicaoDeLink("https://github.com/Lakes777", false), Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"/api/links/{codigo}", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task AChaveContinuaCriandoNaDemo()
    {
        // Em produção, a demo do Pursuit cria links na demo do Beacon
        var cliente = demo.Fabrica.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        cliente.DefaultRequestHeaders.Authorization = new("Bearer", ApiDeTeste.ChaveDeIntegracao);
        var codigo = "k-" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(HttpStatusCode.Created,
            (await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://lakes777.github.io", codigo), Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.DeleteAsync($"/api/links/{codigo}", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task SemearRecusaComADemoDesligada()
    {
        // Na API comum (o "banco real" dos testes): nada pode ser apagado
        var codigo = "s-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HttpStatusCode.Created, (await api.ClienteLogado()
            .PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com", codigo), Cancelar)).StatusCode);

        Assert.Equal(1, await SemeaduraDaDemo.Rodar(api.Services));

        Assert.Equal(HttpStatusCode.OK, (await api.ClienteLogado().GetAsync($"/api/links/{codigo}", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task SemearRodaComADemoLigada() =>
        Assert.Equal(0, await SemeaduraDaDemo.Rodar(demo.Fabrica.Services));

    [Fact]
    public async Task SemeaduraEIdempotenteECoerente()
    {
        // Um link a mais (como os que a demo do Pursuit cria) some na próxima semeadura
        await using (var escopo = demo.Fabrica.Services.CreateAsyncScope())
        {
            var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
            banco.Links.Add(new Link { Codigo = "sobra-" + Guid.NewGuid().ToString("N")[..6], Destino = "https://exemplo.com/" });
            await banco.SaveChangesAsync(Cancelar);
        }

        var primeira = await Semear();
        var segunda = await Semear();

        Assert.Equal(primeira, segunda);
        Assert.InRange(segunda.Links, 9, 11);
        Assert.InRange(segunda.Cliques, 200, 300);

        await using (var escopo = demo.Fabrica.Services.CreateAsyncScope())
        {
            var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
            var links = await banco.Links.AsNoTracking().ToListAsync(Cancelar);
            var cliques = await banco.Cliques.AsNoTracking().ToListAsync(Cancelar);
            var agora = DateTimeOffset.UtcNow;

            Assert.Equal(segunda.Links, links.Count);
            Assert.Equal(segunda.Cliques, cliques.Count);
            Assert.All(links, l => Assert.True(DestinoDoAndre(l.Destino), l.Destino));
            Assert.All(links, l => Assert.Null(Codigos.Problema(l.Codigo)));
            Assert.Contains(links, l => l.Codigo.Length == Codigos.TamanhoGerado && !l.Codigo.Contains('-'));
            var criadoEm = links.ToDictionary(l => l.Codigo, l => l.CriadoEm);
            Assert.All(cliques, c =>
            {
                Assert.True(criadoEm.ContainsKey(c.Codigo));
                Assert.True(c.Momento > criadoEm[c.Codigo]);
                Assert.True(c.Momento <= agora);
                Assert.True(c.Momento >= agora.AddDays(-SemeaduraDaDemo.DiasDeCliques - 1));
            });
            // Cerca de 10% de robôs, e o gráfico sobe: mais cliques na segunda metade do mês
            Assert.InRange(cliques.Count(c => c.Robo), cliques.Count * 0.04, cliques.Count * 0.18);
            var meio = agora.AddDays(-SemeaduraDaDemo.DiasDeCliques / 2.0);
            Assert.True(cliques.Count(c => c.Momento >= meio) > cliques.Count(c => c.Momento < meio));
            Assert.Contains(cliques, c => c.Origem is null);
            Assert.Contains(cliques, c => c.Origem == "www.linkedin.com");
            Assert.True(cliques.Select(c => c.Navegador).Distinct().Count() >= 4);

            // Os códigos das fichas da demo do Pursuit: criados há ~20 dias, com poucos cliques de pessoas e recentes
            foreach (var (codigo, quantos) in new[] { ("k7qm2xz", 4), ("rt4pw9a", 2), ("b3nvx8e", 5) })
            {
                var link = Assert.Single(links, l => l.Codigo == codigo);
                Assert.True(link.Ativo);
                Assert.Equal("https://andrelagos.dev/", link.Destino);
                Assert.InRange(link.CriadoEm, agora.AddDays(-21), agora.AddDays(-19));
                var doLink = cliques.Where(c => c.Codigo == codigo).ToList();
                Assert.Equal(quantos, doLink.Count);
                Assert.All(doLink, c => Assert.False(c.Robo));
                Assert.All(doLink, c => Assert.True(c.Momento >= agora.AddDays(-SemeaduraDaDemo.DiasDeCliquesDoPursuit).AddMinutes(-5)));
            }
        }
    }

    [Fact]
    public async Task SemeaduraApagaACopiaDoCache()
    {
        var codigo = await UmCodigoSemeado();
        var cache = demo.Fabrica.Services.GetRequiredService<CacheDeLinks>();
        await cache.Guardar(codigo, "https://destino-velho.exemplo/");

        await Semear();

        Assert.False((await cache.Buscar(codigo)).Achou);
    }

    private static bool DestinoDoAndre(string destino)
    {
        var uri = new Uri(destino);
        return uri.Scheme == "https" && (uri.Host is "andrelagos.dev" or "lakes777.github.io"
            || (uri.Host == "github.com" && (uri.AbsolutePath == "/Lakes777" || uri.AbsolutePath.StartsWith("/Lakes777/", StringComparison.Ordinal))));
    }

    // ---- Travas da demonstração: contra golpe e contra apagar o banco de verdade ----

    private HttpClient ComChave()
    {
        var cliente = demo.Fabrica.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        cliente.DefaultRequestHeaders.Authorization = new("Bearer", ApiDeTeste.ChaveDeIntegracao);
        return cliente;
    }

    [Fact]
    public async Task NaDemoAChaveSoCriaLinkParaOsSitesDoAndre()
    {
        var cliente = ComChave();

        var golpe = await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://golpe.exemplo/banco", null), Cancelar);
        var portfolio = await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://andrelagos.dev", null), Cancelar);

        Assert.Equal(HttpStatusCode.BadRequest, golpe.StatusCode);
        Assert.Contains("andrelagos.dev", await golpe.Content.ReadAsStringAsync(Cancelar));
        Assert.Equal(HttpStatusCode.Created, portfolio.StatusCode);
    }

    [Fact]
    public async Task VisitanteComAChaveJuntoContinuaSoLendo()
    {
        var cliente = demo.Fabrica.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await cliente.PostAsync("/api/sessao/visitante", null, Cancelar)).StatusCode);
        cliente.DefaultRequestHeaders.Authorization = new("Bearer", ApiDeTeste.ChaveDeIntegracao);

        var resposta = await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://lakes777.github.io", null), Cancelar);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task O403DaChaveNaoEOdoVisitante()
    {
        var resposta = await ComChave().DeleteAsync("/api/links/portfolio-linkedin", Cancelar);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.DoesNotContain("Somente leitura", await resposta.Content.ReadAsStringAsync(Cancelar));
    }

    [Theory]
    [InlineData("https://andrelagos.dev/", true)]
    [InlineData("https://andrelagos.dev/cadence/", true)]
    [InlineData("https://lakes777.github.io/", true)] // o endereço antigo do portfólio, que redireciona ao novo
    [InlineData("https://github.com/Lakes777", true)]
    [InlineData("https://github.com/Lakes777/beacon", true)]
    [InlineData("https://github.com/Lakes7770", false)]
    [InlineData("https://github.com/outro", false)]
    [InlineData("http://lakes777.github.io/", false)]
    [InlineData("https://lakes777.github.io.golpe.exemplo/", false)]
    [InlineData("https://lakes777.github.io:8443/", false)]
    [InlineData("http://andrelagos.dev/", false)]
    [InlineData("https://andrelagos.dev.golpe.exemplo/", false)]
    [InlineData("https://golpe.andrelagos.dev.exemplo/", false)]
    [InlineData("https://beacon.andrelagos.dev/r/x", false)] // um /r/ que leva a outro /r/ esconderia o destino
    public void DestinosPermitidosNaDemo(string destino, bool permitido) =>
        Assert.Equal(permitido, ModoDemo.DestinoPermitido(destino));

    [Theory]
    [InlineData("beacon_demo", true)]
    [InlineData("beacon", false)]
    [InlineData("demo_beacon", false)]
    [InlineData(null, false)]
    public void SoBancoComNomeDeDemo(string? nome, bool demoOk) => Assert.Equal(demoOk, ModoDemo.BancoDeDemo(nome));
}
