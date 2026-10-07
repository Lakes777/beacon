using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beacon.Api.Banco;
using Beacon.Api.Estatisticas;
using Beacon.Api.Links;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api.Tests;

/// <summary>
/// GET /api/links/{codigo}/estatisticas. Os cliques são gravados direto no banco, como o serviço de
/// estatísticas faria, para o teste escolher o momento, o aparelho e a origem de cada um.
/// </summary>
public class EstatisticasTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private readonly HttpClient cliente = api.CreateClient();

    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static DateOnly Hoje => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Brasilia).DateTime);

    /// <summary>Aquele dia e hora no relógio de Brasília, em UTC (como o banco guarda).</summary>
    private static DateTimeOffset EmBrasilia(DateOnly dia, int hora, int minuto = 0)
    {
        var local = dia.ToDateTime(new TimeOnly(hora, minuto));
        return new DateTimeOffset(local, Brasilia.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string CodigoUnico() => "e-" + Guid.NewGuid().ToString("N")[..10];

    private async Task Gravar(Action<BeaconContexto> preparar)
    {
        await using var escopo = api.Services.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        preparar(banco);
        await banco.SaveChangesAsync(Cancelar);
    }

    /// <summary>Link criado há 100 dias, para os cliques antigos do teste valerem.</summary>
    private async Task<string> LinkAntigo()
    {
        var codigo = CodigoUnico();
        await Gravar(b => b.Links.Add(new Link
        {
            Codigo = codigo,
            Destino = "https://exemplo.com/",
            CriadoEm = DateTimeOffset.UtcNow.AddDays(-100),
        }));
        return codigo;
    }

    private static Clique Clique(string codigo, DateTimeOffset momento, string navegador = "Chrome",
        string sistema = "Windows", string aparelho = "computador", string? origem = null, bool robo = false) => new()
        {
            Id = Guid.CreateVersion7(),
            Codigo = codigo,
            Momento = momento,
            Navegador = navegador,
            Sistema = sistema,
            Aparelho = aparelho,
            Origem = origem,
            Robo = robo,
        };

    private async Task<EstatisticasResposta> Estatisticas(string codigo, int? dias = null)
    {
        var resposta = await cliente.GetAsync(
            $"/api/links/{codigo}/estatisticas{(dias is null ? "" : $"?dias={dias}")}", Cancelar);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<EstatisticasResposta>(Cancelar))!;
    }

    [Fact]
    public async Task ContaPorDiaNavegadorSistemaAparelhoEOrigemSemRobos()
    {
        var codigo = await LinkAntigo();
        var anteontem = Hoje.AddDays(-2);
        var ontem = Hoje.AddDays(-1);
        await Gravar(b => b.Cliques.AddRange(
            // 23h30 em Brasília já é o dia seguinte em UTC, mas conta como anteontem
            Clique(codigo, EmBrasilia(anteontem, 23, 30), origem: "www.linkedin.com"),
            Clique(codigo, EmBrasilia(anteontem, 10), navegador: "Firefox", sistema: "Linux"),
            Clique(codigo, EmBrasilia(ontem, 12), sistema: "Android", aparelho: "celular", origem: "www.linkedin.com"),
            Clique(codigo, EmBrasilia(ontem, 12), "Outro", "Outro", "outro", robo: true),
            Clique(codigo, EmBrasilia(ontem, 13), "Outro", "Outro", "outro", "www.linkedin.com", robo: true),
            // De outro link: não entra
            Clique(CodigoUnico(), EmBrasilia(ontem, 12))));

        var numeros = await Estatisticas(codigo.ToUpperInvariant(), dias: 7);

        Assert.Equal(codigo, numeros.Codigo);
        Assert.Equal(3, numeros.Total);
        Assert.Equal(2, numeros.Robos);
        // Os 7 dias, do mais antigo a hoje, inclusive os sem cliques
        Assert.Equal(Enumerable.Range(0, 7).Select(i => Hoje.AddDays(i - 6)), numeros.PorDia.Select(d => d.Dia));
        Assert.Equal([0, 0, 0, 0, 2, 1, 0], numeros.PorDia.Select(d => d.Cliques));
        Assert.Equal([new("Chrome", 2), new("Firefox", 1)], numeros.Navegadores);
        // Empate: ordem alfabética
        Assert.Equal([new("Android", 1), new("Linux", 1), new("Windows", 1)], numeros.Sistemas);
        Assert.Equal([new("computador", 2), new("celular", 1)], numeros.Aparelhos);
        Assert.Equal([new("www.linkedin.com", 2), new("direto", 1)], numeros.Origens);
    }

    [Fact]
    public async Task ODiaComecaEAcabaNaMeiaNoiteDeBrasilia()
    {
        var codigo = await LinkAntigo();
        var ontem = Hoje.AddDays(-1);
        await Gravar(b => b.Cliques.AddRange(
            Clique(codigo, EmBrasilia(ontem, 0, 0)),
            Clique(codigo, EmBrasilia(ontem, 23, 59)),
            // Um minuto antes da meia-noite de ontem: anteontem, fora de "2 dias"
            Clique(codigo, EmBrasilia(ontem, 0, 0).AddMinutes(-1))));

        var numeros = await Estatisticas(codigo, dias: 2);

        Assert.Equal([new(ontem, 2), new(Hoje, 0)], numeros.PorDia);
        Assert.Equal(2, numeros.Total);
    }

    [Fact]
    public async Task OPeriodoPadraoEDe30Dias()
    {
        var codigo = await LinkAntigo();
        await Gravar(b => b.Cliques.AddRange(
            Clique(codigo, EmBrasilia(Hoje.AddDays(-29), 12)),
            Clique(codigo, EmBrasilia(Hoje.AddDays(-30), 12)),
            Clique(codigo, EmBrasilia(Hoje.AddDays(-60), 12))));

        var padrao = await Estatisticas(codigo);
        var ano = await Estatisticas(codigo, dias: 365);

        Assert.Equal(30, padrao.PorDia.Count);
        Assert.Equal(1, padrao.Total);
        Assert.Equal(365, ano.PorDia.Count);
        Assert.Equal(3, ano.Total);
    }

    [Fact]
    public async Task CliquesDeAntesDeOLinkExistirNaoContam()
    {
        // Um clique do link antigo com o mesmo código, gravado depois de apagar (ele ainda estava na fila)
        var codigo = CodigoUnico();
        await Gravar(b =>
        {
            b.Links.Add(new Link { Codigo = codigo, Destino = "https://exemplo.com/", CriadoEm = DateTimeOffset.UtcNow.AddHours(-1) });
            b.Cliques.Add(Clique(codigo, DateTimeOffset.UtcNow.AddHours(-2)));
            b.Cliques.Add(Clique(codigo, DateTimeOffset.UtcNow.AddMinutes(-1)));
        });

        Assert.Equal(1, (await Estatisticas(codigo)).Total);
    }

    [Fact]
    public async Task LinkSemCliquesTemTudoZerado()
    {
        var codigo = await LinkAntigo();

        var numeros = await Estatisticas(codigo, dias: 3);

        Assert.Equal(0, numeros.Total);
        Assert.Equal(0, numeros.Robos);
        Assert.All(numeros.PorDia, d => Assert.Equal(0, d.Cliques));
        Assert.Empty(numeros.Navegadores);
        Assert.Empty(numeros.Origens);
    }

    [Fact]
    public async Task LinkQueNaoExisteDa404()
    {
        var resposta = await cliente.GetAsync($"/api/links/{CodigoUnico()}/estatisticas", Cancelar);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("-5")]
    public async Task DiasForaDoIntervaloDa400(string dias)
    {
        var codigo = await LinkAntigo();

        var resposta = await cliente.GetAsync($"/api/links/{codigo}/estatisticas?dias={dias}", Cancelar);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(Cancelar);
        Assert.True(corpo.GetProperty("errors").TryGetProperty("dias", out _));
    }

    [Fact]
    public async Task DiasQueNaoENumeroDa400()
    {
        var codigo = await LinkAntigo();

        var resposta = await cliente.GetAsync($"/api/links/{codigo}/estatisticas?dias=muitos", Cancelar);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task ApagarOLinkApagaOsCliquesDele()
    {
        var codigo = await LinkAntigo();
        var outro = await LinkAntigo();
        await Gravar(b => b.Cliques.AddRange(
            Clique(codigo, DateTimeOffset.UtcNow), Clique(codigo, DateTimeOffset.UtcNow), Clique(outro, DateTimeOffset.UtcNow)));

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"/api/links/{codigo}", Cancelar)).StatusCode);

        await using var escopo = api.Services.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        Assert.Equal(0, await banco.Cliques.CountAsync(c => c.Codigo == codigo, Cancelar));
        Assert.Equal(1, await banco.Cliques.CountAsync(c => c.Codigo == outro, Cancelar));
        // Reaproveitado, o código começa do zero
        await cliente.PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com/novo", codigo), Cancelar);
        Assert.Equal(0, (await Estatisticas(codigo)).Total);
    }
}
