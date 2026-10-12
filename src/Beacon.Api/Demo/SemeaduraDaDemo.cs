using Beacon.Api.Banco;
using Beacon.Api.Cache;
using Beacon.Api.Estatisticas;
using Beacon.Api.Links;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Demo;

/// <summary>
/// dotnet Beacon.Api.dll semear-demo
/// Apaga os links e os cliques e põe os dados de exemplo da demonstração. Só roda com Demo:Ativo
/// ligado: no banco real, o mesmo comando apagaria tudo. O cron da VM roda de hora em hora, então o
/// que alguém mudar pela chave de integração (a demo do Pursuit) volta ao original.
/// </summary>
public static class SemeaduraDaDemo
{
    public const string Comando = "semear-demo";

    /// <summary>Os links ficam "criados" 40 dias atrás: o período de 30 dias do painel aparece cheio.</summary>
    public const int DiasDesdeACriacao = 40;

    /// <summary>Os cliques ficam nos últimos 30 dias, mais perto de hoje (o gráfico sobe).</summary>
    public const int DiasDeCliques = 30;

    public const int TotalDeCliques = 250;

    /// <summary>Semente fixa: toda hora volta exatamente o mesmo conjunto, só deslocado para o "agora".</summary>
    private const int Semente = 777;

    private const string Portfolio = "https://lakes777.github.io";
    private const string GitHub = "https://github.com/Lakes777";

    /// <summary>
    /// Os links de exemplo: três escolhidos à mão e quatro aleatórios como os que o Pursuit cria por
    /// candidatura. Destinos só do próprio André: a demo nunca leva a um site de outra pessoa.
    /// Peso = parte dos cliques; Origens = de onde as pessoas chegam a ele (null = acesso direto).
    /// </summary>
    private static readonly LinkDeExemplo[] Exemplos =
    [
        new("portfolio-linkedin", Portfolio, true, 30, ["www.linkedin.com", "www.linkedin.com", "lnkd.in", null]),
        new("github-perfil", GitHub, true, 18, ["github.com", "github.com", "www.linkedin.com", null]),
        new("curriculo-geral", Portfolio, true, 16, ["mail.google.com", null, null]),
        new("k7mqp2x", Portfolio, true, 13, ["mail.google.com", "www.linkedin.com", null]),
        new("hw3tbnr", GitHub + "/beacon", true, 10, ["mail.google.com", null]),
        new("p9fjdve", Portfolio, true, 8, ["www.linkedin.com", "lnkd.in", null]),
        // Uma candidatura encerrada: mostra o estado "desativado" na lista
        new("xr4sa8m", Portfolio, false, 5, ["mail.google.com", null]),
    ];

    private sealed record LinkDeExemplo(string Codigo, string Destino, bool Ativo, int Peso, string?[] Origens);

    /// <returns>O código de saída do programa (0 = deu certo).</returns>
    public static async Task<int> Rodar(IServiceProvider servicos)
    {
        if (!ModoDemo.Ativo(servicos.GetRequiredService<IConfiguration>()))
        {
            Console.Error.WriteLine($"{Comando} só roda na demonstração ({ModoDemo.Configuracao} = true): " +
                "ele apaga todos os links e cliques. Nada foi alterado.");
            return 1;
        }
        await using var escopo = servicos.CreateAsyncScope();
        var (links, cliques) = await Semear(escopo.ServiceProvider.GetRequiredService<BeaconContexto>(),
            escopo.ServiceProvider.GetRequiredService<CacheDeLinks>(), DateTimeOffset.UtcNow);
        Console.WriteLine($"Demonstração semeada: {links} links e {cliques} cliques.");
        return 0;
    }

    /// <summary>Na partida da demonstração: banco sem links (recém-criado) ganha os dados de exemplo.</summary>
    public static async Task SemearSeVazio(IServiceProvider servicos, ILogger log)
    {
        await using var escopo = servicos.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        if (await banco.Links.AnyAsync())
        {
            return;
        }
        var (links, cliques) = await Semear(banco, escopo.ServiceProvider.GetRequiredService<CacheDeLinks>(), DateTimeOffset.UtcNow);
        log.LogInformation("Demonstração sem links: semeada com {Links} links e {Cliques} cliques", links, cliques);
    }

    /// <summary>
    /// Troca tudo pelos dados de exemplo, numa transação: quem abre o painel no meio vê o antes ou o
    /// depois, nunca a tabela vazia. Grava direto no banco (e não pela fila): são cliques inventados,
    /// e passar 250 mensagens pelo RabbitMQ a cada hora não testaria nada.
    /// </summary>
    public static async Task<(int Links, int Cliques)> Semear(BeaconContexto banco, CacheDeLinks cache,
        DateTimeOffset agora, CancellationToken cancelar = default)
    {
        // O Npgsql só grava timestamptz em UTC
        agora = agora.ToUniversalTime();
        var criadoEm = agora.AddDays(-DiasDesdeACriacao);
        var sorteio = new Random(Semente);

        var links = Exemplos.Select(e => new Link
        {
            Codigo = e.Codigo,
            // A mesma forma que a API grava (ex.: com a barra no fim)
            Destino = Destinos.Validar(e.Destino).Destino!,
            Ativo = e.Ativo,
            CriadoEm = criadoEm,
        }).ToList();
        var cliques = Enumerable.Range(0, TotalDeCliques).Select(_ => Clique(sorteio, agora)).ToList();

        await using var transacao = await banco.Database.BeginTransactionAsync(cancelar);
        var antigos = await banco.Links.Select(l => l.Codigo).ToListAsync(cancelar);
        await banco.Cliques.ExecuteDeleteAsync(cancelar);
        await banco.Links.ExecuteDeleteAsync(cancelar);
        // De trás para frente: com a mesma data de criação, a lista (do mais novo ao mais antigo, pelo id)
        // mostra os links na ordem de Exemplos, os principais primeiro
        banco.Links.AddRange(Enumerable.Reverse(links));
        banco.Cliques.AddRange(cliques);
        await banco.SaveChangesAsync(cancelar);
        await transacao.CommitAsync(cancelar);

        // Depois de gravar: um link apagado que ficou no Redis continuaria redirecionando por até 10 minutos
        foreach (var codigo in antigos.Union(links.Select(l => l.Codigo)))
        {
            await cache.Esquecer(codigo);
        }
        return (links.Count, cliques.Count);
    }

    private static Clique Clique(Random sorteio, DateTimeOffset agora)
    {
        var link = Sortear(sorteio, Exemplos, e => e.Peso);
        // Raiz quadrada de um número uniforme: mais cliques perto de hoje, como um link que vai sendo
        // espalhado. Até 1 minuto atrás, para nunca cair no futuro. O link desativado parou de receber
        // cliques 12 dias atrás (desativado, ele dá 404 e não conta mais nada)
        var fracao = Math.Sqrt(sorteio.NextDouble());
        var dias = link.Ativo ? DiasDeCliques : DiasDeCliques - 12;
        var momento = agora.AddDays(-DiasDeCliques).AddMinutes(fracao * (dias * 24 * 60 - 1));
        var id = Guid.CreateVersion7(momento);

        // ~10% são a prévia do LinkedIn (o robô abre o link quando ele é colado num post ou numa mensagem)
        if (sorteio.NextDouble() < 0.1)
        {
            return new Clique
            {
                Id = id,
                Codigo = link.Codigo,
                Momento = momento,
                Robo = true,
                Navegador = "LinkedInBot",
                Sistema = "Outro",
                Aparelho = "outro",
                Origem = null,
            };
        }
        var origem = link.Origens[sorteio.Next(link.Origens.Length)];
        var (aparelho, sistema, navegador) = sorteio.NextDouble() switch
        {
            < 0.40 => ("computador", "Windows", Sortear(sorteio, [("Chrome", 6), ("Edge", 3), ("Firefox", 1)])),
            < 0.52 => ("computador", "macOS", Sortear(sorteio, [("Safari", 1), ("Chrome", 1)])),
            < 0.58 => ("computador", "Linux", Sortear(sorteio, [("Firefox", 1), ("Chrome", 1)])),
            < 0.82 => ("celular", "Android", Sortear(sorteio, [("Chrome", 4), ("Samsung Internet", 1)])),
            < 0.97 => ("celular", "iOS", "Safari"),
            _ => ("tablet", "iOS", "Safari"),
        };
        return new Clique
        {
            Id = id,
            Codigo = link.Codigo,
            Momento = momento,
            Robo = false,
            Navegador = navegador,
            Sistema = sistema,
            Aparelho = aparelho,
            Origem = origem,
        };
    }

    private static string Sortear(Random sorteio, (string Valor, int Peso)[] opcoes) =>
        Sortear(sorteio, opcoes, o => o.Peso).Valor;

    private static T Sortear<T>(Random sorteio, IReadOnlyList<T> opcoes, Func<T, int> peso)
    {
        var alvo = sorteio.Next(opcoes.Sum(peso));
        foreach (var opcao in opcoes)
        {
            alvo -= peso(opcao);
            if (alvo < 0)
            {
                return opcao;
            }
        }
        return opcoes[^1];
    }
}
