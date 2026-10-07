using System.Text;
using Beacon.Contratos;
using RabbitMQ.Client;

namespace Beacon.Estatisticas.Tests;

/// <summary>
/// O caminho inteiro: publica na exchange como a API, o serviço consome e grava no Postgres.
/// Os testes de uma classe rodam um depois do outro (xUnit), e cada um usa um código só seu.
/// </summary>
public class ConsumidorTest(ServicoDeTeste servico)
{
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(20);

    private CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private const string ChromeNoWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private static string CodigoUnico() => "teste-" + Guid.NewGuid().ToString("N")[..8];

    private async Task PublicarAsync(byte[] corpo) =>
        await servico.Canal.BasicPublishAsync(Topologia.Exchange, Topologia.ChaveDoClique, mandatory: true,
            new BasicProperties { Persistent = true, ContentType = "application/json" }, corpo, Cancelar);

    private async Task<long> ContarAsync(string sql, object valor)
    {
        await using var comando = servico.Banco.CreateCommand(sql);
        comando.Parameters.Add(new() { Value = valor });
        return (long)(await comando.ExecuteScalarAsync(Cancelar))!;
    }

    /// <summary>A fila é assíncrona: espera a linha aparecer, com prazo.</summary>
    private async Task EsperarLinhaAsync(Guid id)
    {
        var limite = DateTime.UtcNow + Prazo;
        while (await ContarAsync("SELECT count(*) FROM clique WHERE id = $1", id) == 0)
        {
            Assert.True(DateTime.UtcNow < limite, $"O clique {id} não foi gravado em {Prazo.TotalSeconds} s");
            await Task.Delay(100, Cancelar);
        }
    }

    [Fact]
    public async Task UmCliquePublicadoViraUmaLinhaClassificada()
    {
        var momento = new DateTimeOffset(2026, 10, 7, 15, 30, 0, TimeSpan.FromHours(-3));
        var clique = new CliqueRegistrado(Guid.NewGuid(), CodigoUnico(), momento, ChromeNoWindows,
            "https://www.linkedin.com/feed/");

        await PublicarAsync(clique.ParaBytes());
        await EsperarLinhaAsync(clique.Id);

        await using var comando = servico.Banco.CreateCommand(
            "SELECT codigo, momento, navegador, sistema, aparelho, origem, robo FROM clique WHERE id = $1");
        comando.Parameters.Add(new() { Value = clique.Id });
        await using var linha = await comando.ExecuteReaderAsync(Cancelar);
        Assert.True(await linha.ReadAsync(Cancelar));
        Assert.Equal(clique.Codigo, linha.GetString(0));
        // O Postgres devolve em UTC; o instante tem de ser o mesmo
        Assert.Equal(momento, linha.GetFieldValue<DateTimeOffset>(1));
        Assert.Equal("Chrome", linha.GetString(2));
        Assert.Equal("Windows", linha.GetString(3));
        Assert.Equal("computador", linha.GetString(4));
        Assert.Equal("www.linkedin.com", linha.GetString(5));
        Assert.False(linha.GetBoolean(6));
    }

    [Fact]
    public async Task SemRefererAOrigemFicaNula()
    {
        var clique = new CliqueRegistrado(Guid.NewGuid(), CodigoUnico(), DateTimeOffset.UtcNow, "WhatsApp/2.23.20.0", null);

        await PublicarAsync(clique.ParaBytes());
        await EsperarLinhaAsync(clique.Id);

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM clique WHERE id = $1 AND origem IS NULL AND robo", clique.Id));
    }

    [Fact]
    public async Task AMesmaMensagemDuasVezesViraUmaLinhaSo()
    {
        var codigo = CodigoUnico();
        var repetido = new CliqueRegistrado(Guid.NewGuid(), codigo, DateTimeOffset.UtcNow, ChromeNoWindows, null);
        var depois = repetido with { Id = Guid.NewGuid() };

        await PublicarAsync(repetido.ParaBytes());
        await PublicarAsync(repetido.ParaBytes());
        // O consumidor processa uma por vez, na ordem da fila: quando a última aparece no banco,
        // as duas cópias já passaram
        await PublicarAsync(depois.ParaBytes());
        await EsperarLinhaAsync(depois.Id);

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM clique WHERE id = $1", repetido.Id));
        Assert.Equal(2, await ContarAsync("SELECT count(*) FROM clique WHERE codigo = $1", codigo));
    }

    [Fact]
    public async Task GravarDeNovoOMesmoCliqueNaoDaErro()
    {
        // Sem o ON CONFLICT, a segunda vez daria erro de chave duplicada e a cópia iria para a
        // dead letter: a contagem ficaria certa, mas a fila dos mortos se encheria de falsos problemas
        var gravador = new GravadorDeCliques(servico.Banco);
        var clique = new CliqueRegistrado(Guid.NewGuid(), CodigoUnico(), DateTimeOffset.UtcNow, ChromeNoWindows, null);

        Assert.True(await gravador.GravarAsync(clique, Cancelar));
        Assert.False(await gravador.GravarAsync(clique, Cancelar));
    }

    [Fact]
    public async Task ComOBancoFalhandoAMensagemVoltaParaAFilaEEntraDepois()
    {
        // Simula o banco recusando o INSERT (tabela "sumida"), sem derrubar o contêiner
        var clique = new CliqueRegistrado(Guid.NewGuid(), CodigoUnico(), DateTimeOffset.UtcNow, ChromeNoWindows, null);
        await ExecutarAsync("ALTER TABLE clique RENAME TO clique_fora");
        try
        {
            await PublicarAsync(clique.ParaBytes());
            // Tempo para a primeira tentativa falhar e a mensagem voltar para a fila
            await Task.Delay(1500, Cancelar);
            Assert.Equal(0, await ContarAsync("SELECT count(*) FROM clique_fora WHERE id = $1", clique.Id));
        }
        finally
        {
            await ExecutarAsync("ALTER TABLE clique_fora RENAME TO clique");
        }

        // Não foi para a dead letter: com o banco de volta, o clique entra
        await EsperarLinhaAsync(clique.Id);
    }

    private async Task ExecutarAsync(string sql)
    {
        await using var comando = servico.Banco.CreateCommand(sql);
        await comando.ExecuteNonQueryAsync(Cancelar);
    }

    [Theory]
    // CODIGO vira um código único (para achar a mensagem e conferir que nada foi gravado) e ID, um Guid novo
    [InlineData("isto não é JSON CODIGO")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000000","codigo":"CODIGO","momento":"2026-10-07T12:00:00Z"}""")]
    [InlineData("""{"id":"ID","codigo":"","momento":"2026-10-07T12:00:00Z","userAgent":"CODIGO"}""")]
    [InlineData("""{"id":"ID","codigo":"CODIGO-mais-longo-do-que-os-64-caracteres-da-coluna-codigo-xxxxxxxxxx","momento":"2026-10-07T12:00:00Z"}""")]
    public async Task MensagemInvalidaVaiParaAFilaDosMortosSemGravar(string modelo)
    {
        var codigo = CodigoUnico();
        var corpo = Encoding.UTF8.GetBytes(modelo.Replace("CODIGO", codigo).Replace("ID", Guid.NewGuid().ToString()));

        await PublicarAsync(corpo);

        Assert.True(await AchouNaFilaDosMortosAsync(corpo), "A mensagem inválida não chegou à fila dos mortos");
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM clique WHERE codigo LIKE $1", codigo + "%"));
    }

    private async Task<bool> AchouNaFilaDosMortosAsync(byte[] corpo)
    {
        var limite = DateTime.UtcNow + Prazo;
        while (DateTime.UtcNow < limite)
        {
            var mensagem = await servico.Canal.BasicGetAsync(Topologia.FilaDosMortos, autoAck: true, Cancelar);
            if (mensagem is null)
            {
                await Task.Delay(100, Cancelar);
            }
            else if (mensagem.Body.Span.SequenceEqual(corpo))
            {
                return true;
            }
        }

        return false;
    }
}
