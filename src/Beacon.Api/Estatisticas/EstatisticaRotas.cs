using System.Linq.Expressions;
using Beacon.Api.Banco;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Estatisticas;

/// <summary>
/// Os números de um link, calculados a partir da tabela clique (que o serviço de estatísticas grava).
/// Todas as contas são feitas no banco (GROUP BY, COUNT): um link com milhões de cliques não vem
/// inteiro para a memória da API.
/// </summary>
public static class EstatisticaRotas
{
    public const int DiasPadrao = 30;
    public const int MaximoDeDias = 365;

    /// <summary>Os dias são contados no horário de Brasília: um clique às 23h30 é daquele dia, e não do seguinte (UTC).</summary>
    public const string Fuso = "America/Sao_Paulo";

    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.FindSystemTimeZoneById(Fuso);

    public static void MapearEstatisticas(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/links/{codigo}/estatisticas", Calcular)
            .WithTags("Links")
            .WithSummary($"Cliques do link nos últimos dias (1 a {MaximoDeDias}; padrão {DiasPadrao}), sem contar robôs");
        app.MapGet("/api/estatisticas", Resumir)
            .WithTags("Links")
            .WithSummary("Cliques de todos os links juntos no período, por dia e por link (o topo do painel)");
    }

    /// <summary>
    /// Os números do painel inteiro: total, cliques por dia e quantos cliques cada link teve.
    /// Os cliques entram pela junção com link: um clique de um código apagado (ou de antes de o link
    /// atual existir, se o código foi reaproveitado) fica de fora, como em Calcular.
    /// </summary>
    private static async Task<Results<Ok<ResumoResposta>, ValidationProblem>> Resumir(
        BeaconContexto banco, CancellationToken cancelar, int dias = DiasPadrao)
    {
        if (DiasInvalidos(dias) is { } problema)
        {
            return problema;
        }
        var dia = Hoje().AddDays(-(dias - 1));
        var inicio = InicioDoDia(dia);
        var fim = InicioDoDia(Hoje().AddDays(1));
        var doPeriodo =
            from c in banco.Cliques.AsNoTracking()
            join l in banco.Links.AsNoTracking() on c.Codigo equals l.Codigo
            where c.Momento >= inicio && c.Momento < fim && c.Momento >= l.CriadoEm
            select c;
        var dePessoas = doPeriodo.Where(c => !c.Robo);

        var robos = await doPeriodo.CountAsync(c => c.Robo, cancelar);
        var porLink = await dePessoas.GroupBy(c => c.Codigo)
            .Select(g => new { Codigo = g.Key, Cliques = g.Count() })
            .ToDictionaryAsync(g => g.Codigo, g => g.Cliques, cancelar);
        var porDia = await ContarPorDia(dePessoas, dia, dias, cancelar);

        return TypedResults.Ok(new ResumoResposta(
            Total: porLink.Values.Sum(),
            Robos: robos,
            porDia,
            // Todo link aparece, inclusive os sem cliques no período: o painel não precisa completar
            PorLink: (await banco.Links.AsNoTracking().Select(l => l.Codigo).ToListAsync(cancelar))
                .Select(codigo => new CliquesDoLink(codigo, porLink.GetValueOrDefault(codigo)))
                .OrderByDescending(l => l.Cliques).ThenBy(l => l.Codigo)
                .ToList()));
    }

    private static ValidationProblem? DiasInvalidos(int dias) => dias is < 1 or > MaximoDeDias
        ? TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["dias"] = [$"Use de 1 a {MaximoDeDias} dias."],
        })
        : null;

    /// <summary>
    /// Um item por dia, do primeiro a hoje, com 0 nos dias sem clique (o gráfico não precisa adivinhar
    /// os buracos). Vira "momento AT TIME ZONE 'America/Sao_Paulo'" no Postgres: cada clique vira a data
    /// e hora de Brasília antes de cortar o dia. (O Npgsql só traduz a versão com DateTime, daí o UtcDateTime.)
    /// </summary>
    private static async Task<List<CliquesNoDia>> ContarPorDia(IQueryable<Clique> cliques, DateOnly primeiro,
        int dias, CancellationToken cancelar)
    {
        var contagem = await cliques
            .GroupBy(c => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(c.Momento.UtcDateTime, Fuso).Date)
            .Select(g => new { Dia = g.Key, Cliques = g.Count() })
            .ToDictionaryAsync(d => DateOnly.FromDateTime(d.Dia), d => d.Cliques, cancelar);
        return Enumerable.Range(0, dias)
            .Select(i => primeiro.AddDays(i))
            .Select(d => new CliquesNoDia(d, contagem.GetValueOrDefault(d)))
            .ToList();
    }

    private static async Task<Results<Ok<EstatisticasResposta>, NotFound, ValidationProblem>> Calcular(
        string codigo, BeaconContexto banco, CancellationToken cancelar, int dias = DiasPadrao)
    {
        if (DiasInvalidos(dias) is { } problema)
        {
            return problema;
        }
        var normalizado = Codigos.Normalizar(codigo);
        var link = await banco.Links.AsNoTracking()
            .Where(l => l.Codigo == normalizado)
            .Select(l => new { l.CriadoEm })
            .SingleOrDefaultAsync(cancelar);
        if (link is null)
        {
            return TypedResults.NotFound();
        }

        var dia = Hoje().AddDays(-(dias - 1));
        // Só cliques de depois da criação: se o código foi de um link apagado, um clique dele que
        // ainda estava na fila pode ter sido gravado depois de apagar, e não é deste link
        var inicio = Max(InicioDoDia(dia), link.CriadoEm);
        // Até o fim de hoje: um clique com horário no futuro (relógio errado) faria o total não bater com os dias
        var fim = InicioDoDia(Hoje().AddDays(1));
        var doPeriodo = banco.Cliques.AsNoTracking()
            .Where(c => c.Codigo == normalizado && c.Momento >= inicio && c.Momento < fim);
        var dePessoas = doPeriodo.Where(c => !c.Robo);

        var porTipo = await doPeriodo.GroupBy(c => c.Robo)
            .Select(g => new { Robo = g.Key, Cliques = g.Count() })
            .ToListAsync(cancelar);
        var porDia = await ContarPorDia(dePessoas, dia, dias, cancelar);

        return TypedResults.Ok(new EstatisticasResposta(
            normalizado,
            Total: porTipo.Where(t => !t.Robo).Sum(t => t.Cliques),
            Robos: porTipo.Where(t => t.Robo).Sum(t => t.Cliques),
            porDia,
            Navegadores: await Contar(dePessoas, c => c.Navegador, cancelar),
            Sistemas: await Contar(dePessoas, c => c.Sistema, cancelar),
            Aparelhos: await Contar(dePessoas, c => c.Aparelho, cancelar),
            Origens: await Contar(dePessoas, c => c.Origem ?? "direto", cancelar)));
    }

    /// <summary>Quantos cliques por valor do campo, do mais clicado ao menos (empate: ordem alfabética).</summary>
    private static Task<List<Contagem>> Contar(IQueryable<Clique> cliques, Expression<Func<Clique, string>> campo,
        CancellationToken cancelar) =>
        cliques.GroupBy(campo)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new Contagem(g.Key, g.Count()))
            .ToListAsync(cancelar);

    private static DateOnly Hoje() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Brasilia).DateTime);

    /// <summary>Meia-noite daquele dia em Brasília, em UTC (o Npgsql só aceita UTC em timestamptz).</summary>
    internal static DateTimeOffset InicioDoDia(DateOnly dia)
    {
        var meiaNoite = dia.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(meiaNoite, Brasilia.GetUtcOffset(meiaNoite)).ToUniversalTime();
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
