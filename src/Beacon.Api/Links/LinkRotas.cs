using Beacon.Api.Banco;
using Beacon.Api.Cache;
using Beacon.Api.Mensageria;
using Beacon.Contratos;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Beacon.Api.Links;

/// <summary>
/// As rotas dos links. Cada método é o "handler" de uma rota: o ASP.NET entrega o que ele pede
/// nos parâmetros (o corpo em JSON, o banco pela injeção de dependência, o código da URL).
/// </summary>
public static class LinkRotas
{
    /// <summary>Tentativas de gerar um código aleatório que ainda não exista.</summary>
    private const int TentativasDeCodigo = 5;

    public static void MapearLinks(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/api/links").WithTags("Links");
        links.MapPost("/", Criar).WithSummary("Cria um link curto (código opcional: sem ele, um aleatório é gerado)");
        links.MapGet("/", Listar).WithSummary("Lista os links, do mais novo ao mais antigo");
        links.MapGet("/{codigo}", Buscar).WithSummary("Busca um link pelo código");
        links.MapPut("/{codigo}", Editar).WithSummary("Muda o destino e, se informado, ativa ou desativa o link");
        links.MapDelete("/{codigo}", Apagar).WithSummary("Apaga um link");

        app.MapGet("/r/{codigo}", Redirecionar)
            .WithTags("Redirecionamento")
            .WithSummary("Leva ao destino do link (302); 404 se não existe ou está desativado");
    }

    private static async Task<Results<Created<LinkResposta>, ValidationProblem, ProblemHttpResult>> Criar(
        NovoLink pedido, BeaconContexto banco, CacheDeLinks cache, HttpRequest requisicao, CancellationToken cancelar)
    {
        var erros = new Dictionary<string, string[]>();
        var (destino, problemaDestino) = Destinos.Validar(pedido.Destino);
        if (problemaDestino is not null)
        {
            erros["destino"] = [problemaDestino];
        }
        var escolhido = pedido.Codigo is null ? null : Codigos.Normalizar(pedido.Codigo);
        if (escolhido is not null && Codigos.Problema(escolhido) is { } problemaCodigo)
        {
            erros["codigo"] = [problemaCodigo];
        }
        if (erros.Count > 0)
        {
            return TypedResults.ValidationProblem(erros);
        }

        // O caso comum (código já usado) é respondido sem tentar inserir, o que deixaria um erro no log.
        // Mas quem garante de verdade é o índice único do banco: se dois pedidos com o mesmo código
        // passarem juntos por esta consulta, só um INSERT vence, e o outro cai no catch abaixo.
        if (escolhido is not null && await banco.Links.AnyAsync(l => l.Codigo == escolhido, cancelar))
        {
            return CodigoEmUso(escolhido);
        }

        var link = await Salvar(banco, destino!, escolhido, Codigos.Gerar, cancelar);
        if (link is null)
        {
            return CodigoEmUso(escolhido!);
        }
        // Alguém pode ter tentado esse código antes de ele existir: apaga o "não existe" guardado
        await cache.Esquecer(link.Codigo);
        return TypedResults.Created($"/api/links/{link.Codigo}", Resposta(link, requisicao));
    }

    /// <summary>
    /// Grava o link. Com código escolhido já em uso, devolve null. Com código gerado repetido
    /// (raríssimo), sorteia outro e tenta de novo. O gerador vem por parâmetro para o teste
    /// conseguir forçar uma repetição.
    /// </summary>
    internal static async Task<Link?> Salvar(BeaconContexto banco, string destino, string? escolhido,
        Func<string> gerarCodigo, CancellationToken cancelar)
    {
        var link = new Link { Codigo = escolhido ?? gerarCodigo(), Destino = destino };
        banco.Links.Add(link);
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                await banco.SaveChangesAsync(cancelar);
                return link;
            }
            catch (DbUpdateException erro) when (CodigoRepetido(erro))
            {
                if (escolhido is not null)
                {
                    // Tira o link do contexto: senão um próximo SaveChanges tentaria inserir de novo
                    banco.Entry(link).State = EntityState.Detached;
                    return null;
                }
                if (tentativa == TentativasDeCodigo)
                {
                    throw;
                }
                // A entidade continua "a inserir" no contexto: trocar o código basta para tentar de novo
                link.Codigo = gerarCodigo();
            }
        }
    }

    private static async Task<Ok<List<LinkResposta>>> Listar(
        BeaconContexto banco, HttpRequest requisicao, CancellationToken cancelar)
    {
        var links = await banco.Links.AsNoTracking()
            .OrderByDescending(l => l.CriadoEm).ThenByDescending(l => l.Id)
            .ToListAsync(cancelar);
        return TypedResults.Ok(links.Select(l => Resposta(l, requisicao)).ToList());
    }

    private static async Task<Results<Ok<LinkResposta>, NotFound>> Buscar(
        string codigo, BeaconContexto banco, HttpRequest requisicao, CancellationToken cancelar)
    {
        var link = await Achar(banco, codigo, cancelar);
        return link is null ? TypedResults.NotFound() : TypedResults.Ok(Resposta(link, requisicao));
    }

    private static async Task<Results<Ok<LinkResposta>, NotFound, ValidationProblem>> Editar(
        string codigo, EdicaoDeLink pedido, BeaconContexto banco, CacheDeLinks cache, HttpRequest requisicao,
        CancellationToken cancelar)
    {
        var (destino, problema) = Destinos.Validar(pedido.Destino);
        if (problema is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["destino"] = [problema] });
        }
        var link = await Achar(banco, codigo, cancelar);
        if (link is null)
        {
            return TypedResults.NotFound();
        }
        link.Destino = destino!;
        link.Ativo = pedido.Ativo ?? link.Ativo;
        await banco.SaveChangesAsync(cancelar);
        // Depois de gravar no banco, nunca antes: senão um clique no meio poderia guardar o destino velho de novo
        await cache.Esquecer(link.Codigo);
        return TypedResults.Ok(Resposta(link, requisicao));
    }

    private static async Task<Results<NoContent, NotFound>> Apagar(
        string codigo, BeaconContexto banco, CacheDeLinks cache, CancellationToken cancelar)
    {
        // Apaga direto no banco, sem carregar o link antes. Os cliques vão junto, na mesma transação:
        // o código pode ser reaproveitado por outro link, que não deve herdar os números deste.
        var normalizado = Codigos.Normalizar(codigo);
        await using var transacao = await banco.Database.BeginTransactionAsync(cancelar);
        var apagados = await banco.Links.Where(l => l.Codigo == normalizado)
            .ExecuteDeleteAsync(cancelar);
        if (apagados == 0)
        {
            return TypedResults.NotFound();
        }
        await banco.Cliques.Where(c => c.Codigo == normalizado).ExecuteDeleteAsync(cancelar);
        await transacao.CommitAsync(cancelar);
        await cache.Esquecer(normalizado);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// 302 (temporário), e não 301 (permanente): o navegador guardaria o 301 para sempre e
    /// pararia de passar pelo Beacon, então trocar o destino e contar os cliques não funcionaria.
    /// Consulta o Redis primeiro; só vai ao banco se ele não souber (o cabeçalho X-Beacon-Cache diz qual foi).
    /// </summary>
    private static async Task<Results<RedirectHttpResult, NotFound>> Redirecionar(
        string codigo, BeaconContexto banco, CacheDeLinks cache, FilaDeCliques fila, HttpRequest requisicao,
        HttpResponse resposta, IHostEnvironment ambiente, CancellationToken cancelar)
    {
        var normalizado = Codigos.Normalizar(codigo);
        // Um texto que nunca poderia ser código não vai nem ao cache nem ao banco: senão um robô
        // mandando /r/ + milhares de caracteres aleatórios encheria o Redis de "não existe"
        if (Codigos.Problema(normalizado) is not null)
        {
            return TypedResults.NotFound();
        }
        // HIT/MISS ajuda a testar e a explicar, mas em produção contaria a qualquer visitante
        // se aquele link foi clicado nos últimos minutos
        var mostrarCache = !ambiente.IsProduction();
        var guardado = await cache.Buscar(normalizado);
        string? destino;
        if (guardado.Achou)
        {
            if (mostrarCache)
            {
                resposta.Headers["X-Beacon-Cache"] = "HIT";
            }
            destino = guardado.Destino;
        }
        else
        {
            if (mostrarCache)
            {
                resposta.Headers["X-Beacon-Cache"] = "MISS";
            }
            destino = await banco.Links.AsNoTracking()
                .Where(l => l.Codigo == normalizado && l.Ativo)
                .Select(l => l.Destino)
                .SingleOrDefaultAsync(cancelar);
            // Guarda também o "não existe" (por pouco tempo): tentativas repetidas não vão ao banco
            await cache.Guardar(normalizado, destino);
        }
        if (destino is null)
        {
            return TypedResults.NotFound();
        }
        // Só deixa o clique na fila em memória (não espera o RabbitMQ): o redirecionamento sai na hora
        fila.Entregar(new CliqueRegistrado(Guid.CreateVersion7(), normalizado, DateTimeOffset.UtcNow,
            Cabecalho(requisicao.Headers.UserAgent), SemConsulta(Cabecalho(requisicao.Headers.Referer))));
        return TypedResults.Redirect(destino);
    }

    /// <summary>
    /// Até 1.024 caracteres: cabeçalhos podem ter dezenas de KB, e 10.000 cliques gigantes esperando
    /// na fila em memória (RabbitMQ fora do ar) pesariam centenas de MB. Vazio vira null.
    /// </summary>
    /// <summary>
    /// Só o endereço, sem o que vem depois de "?" ou "#": ali costumam ir tokens, e-mails e buscas de
    /// quem clicou, e uma mensagem na dead letter guardaria isso sem prazo. As estatísticas só usam o domínio.
    /// </summary>
    internal static string? SemConsulta(string? referer)
    {
        var corte = referer?.IndexOfAny(['?', '#']) ?? -1;
        return corte < 0 ? referer : referer![..corte] is { Length: > 0 } resto ? resto : null;
    }

    internal static string? Cabecalho(Microsoft.Extensions.Primitives.StringValues valor)
    {
        var texto = valor.ToString();
        return texto.Length == 0 ? null : texto.Length > TamanhoDoCabecalho ? texto[..TamanhoDoCabecalho] : texto;
    }

    internal const int TamanhoDoCabecalho = 1024;

    private static Task<Link?> Achar(BeaconContexto banco, string codigo, CancellationToken cancelar)
    {
        var normalizado = Codigos.Normalizar(codigo);
        return banco.Links.SingleOrDefaultAsync(l => l.Codigo == normalizado, cancelar);
    }

    private static ProblemHttpResult CodigoEmUso(string codigo) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Código em uso",
        detail: $"Já existe um link com o código \"{codigo}\".");

    private static bool CodigoRepetido(DbUpdateException erro) =>
        erro.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static LinkResposta Resposta(Link link, HttpRequest requisicao) => new(
        link.Codigo, link.Destino, link.Ativo, link.CriadoEm,
        $"{requisicao.Scheme}://{requisicao.Host}{requisicao.PathBase}/r/{link.Codigo}");
}
