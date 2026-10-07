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
    }

    private static async Task<Results<Ok<EstatisticasResposta>, NotFound, ValidationProblem>> Calcular(
        string codigo, BeaconContexto banco, CancellationToken cancelar, int dias = DiasPadrao)
    {
        if (dias is < 1 or > MaximoDeDias)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["dias"] = [$"Use de 1 a {MaximoDeDias} dias."],
            });
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
        var doPeriodo = banco.Cliques.AsNoTracking().Where(c => c.Codigo == normalizado && c.Momento >= inicio);
        var dePessoas = doPeriodo.Where(c => !c.Robo);

        var porTipo = await doPeriodo.GroupBy(c => c.Robo)
            .Select(g => new { Robo = g.Key, Cliques = g.Count() })
            .ToListAsync(cancelar);
        // Vira "momento AT TIME ZONE 'America/Sao_Paulo'" no Postgres: cada clique vira a data e hora
        // de Brasília antes de cortar o dia. (O Npgsql só traduz a versão com DateTime, daí o UtcDateTime.)
        var contagemPorDia = await dePessoas
            .GroupBy(c => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(c.Momento.UtcDateTime, Fuso).Date)
            .Select(g => new { Dia = g.Key, Cliques = g.Count() })
            .ToDictionaryAsync(d => DateOnly.FromDateTime(d.Dia), d => d.Cliques, cancelar);
        // Os dias sem clique também aparecem (com 0): o gráfico não precisa adivinhar os buracos
        var porDia = Enumerable.Range(0, dias)
            .Select(i => dia.AddDays(i))
            .Select(d => new CliquesNoDia(d, contagemPorDia.GetValueOrDefault(d)))
            .ToList();

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
