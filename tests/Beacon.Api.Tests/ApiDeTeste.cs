using System.Net.Http.Json;
using Beacon.Api.Banco;
using Beacon.Api.Contas;
using Beacon.Api.Mensageria;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

// Um Postgres e uma API para todos os testes do projeto (subir o contêiner leva alguns segundos)
[assembly: AssemblyFixture(typeof(Beacon.Api.Tests.ApiDeTeste))]

namespace Beacon.Api.Tests;

/// <summary>
/// Sobe a API inteira, em memória, ligada a um Postgres, um Redis e um RabbitMQ de verdade, em
/// contêineres (Testcontainers). É o equivalente ao @SpringBootTest + Testcontainers do Vigil.
/// </summary>
public class ApiDeTeste : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer banco = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").Build();
    // Sem o painel de administração (o "-management" do compose): os testes não precisam dele
    private readonly RabbitMqContainer rabbitmq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    /// <summary>A conta que os testes usam para entrar no painel (criada ao subir).</summary>
    public const string Usuario = "teste";
    public const string Senha = "senha-dos-testes";

    /// <summary>O cookie de uma sessão aberta ao subir, reaproveitado por ClienteLogado.</summary>
    private string cookieDaSessao = "";

    /// <summary>Para os testes que simulam o Redis fora do ar usarem o mesmo banco.</summary>
    public string ConexaoDoBanco => banco.GetConnectionString();

    /// <summary>Para os testes lerem a fila direto no RabbitMQ.</summary>
    public string ConexaoDoRabbitMQ => rabbitmq.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(banco.StartAsync(), redis.StartAsync(), rabbitmq.StartAsync());

        // A API conecta ao RabbitMQ em segundo plano. Espera a conexão abrir: senão o primeiro
        // teste do /saude poderia ver "Degraded" só por ter chegado antes dela
        var publicador = Services.GetRequiredService<PublicadorDeCliques>();
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (!publicador.Conectado)
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException("A API não conectou ao RabbitMQ do teste em 30 s.");
            }
            await Task.Delay(50);
        }

        await using (var escopo = Services.CreateAsyncScope())
        {
            await RegrasDeConta.DefinirSenha(escopo.ServiceProvider.GetRequiredService<BeaconContexto>(),
                escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(), Usuario, Senha);
        }
        var resposta = await CreateClient().PostAsJsonAsync("/api/sessao", new Login(Usuario, Senha));
        resposta.EnsureSuccessStatusCode();
        // "beacon_sessao=...; expires=...; path=/api; ..." -> só o "nome=valor"
        cookieDaSessao = resposta.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    /// <summary>Um cliente já logado (as rotas de /api/links pedem login).</summary>
    public HttpClient ClienteLogado(bool seguirRedirecionamentos = true)
    {
        // Sem o controle de cookies do cliente: o cookie vai fixo em todo pedido
        var cliente = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = seguirRedirecionamentos,
            HandleCookies = false,
        });
        cliente.DefaultRequestHeaders.Add("Cookie", cookieDaSessao);
        return cliente;
    }

    /// <summary>Entra pela rota de login numa outra API (as criadas com WithWebHostBuilder) e devolve o cliente logado.</summary>
    public static async Task<HttpClient> Entrar(WebApplicationFactory<Program> fabrica, bool seguirRedirecionamentos = true)
    {
        // Este cliente guarda os cookies recebidos e os manda de volta, como um navegador
        var cliente = fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = seguirRedirecionamentos });
        var resposta = await cliente.PostAsJsonAsync("/api/sessao", new Login(Usuario, Senha));
        resposta.EnsureSuccessStatusCode();
        return cliente;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ambiente próprio: sem ele, a API leria o appsettings.Development.json (o banco do dia a dia).
        // Se a conexão abaixo deixar de valer, a API nem sobe e o teste quebra na hora.
        builder.UseEnvironment("Testes");
        builder.UseSetting("ConnectionStrings:Banco", banco.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", redis.GetConnectionString());
        builder.UseSetting("ConnectionStrings:RabbitMQ", rabbitmq.GetConnectionString());
        // Os testes fazem muitos logins seguidos; o limite em si é testado em SessaoTest com outro valor
        builder.UseSetting("Login:TentativasPorMinuto", "100000");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await banco.DisposeAsync();
        await redis.DisposeAsync();
        await rabbitmq.DisposeAsync();
    }
}
