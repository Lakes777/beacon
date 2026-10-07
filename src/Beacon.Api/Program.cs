using Beacon.Api.Banco;
using Beacon.Api.Cache;
using Beacon.Api.Estatisticas;
using Beacon.Api.Links;
using Beacon.Api.Mensageria;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Melhor a API nem subir do que subir sem banco e falhar no primeiro pedido
var conexao = builder.Configuration.GetConnectionString("Banco")
    ?? throw new InvalidOperationException(
        "Defina a conexão com o banco em ConnectionStrings:Banco (variável ConnectionStrings__Banco).");

// Injeção de dependência: registra o contexto do banco, e quem precisar dele recebe pelo construtor
builder.Services.AddDbContext<BeaconContexto>(opcoes => opcoes
    .UseNpgsql(conexao)
    .UseSnakeCaseNamingConvention());

// Redis (cache do redirecionamento). Uma conexão só para a API inteira (singleton), que o
// StackExchange.Redis compartilha entre os pedidos. AbortOnConnectFail = false: a API sobe mesmo
// com o Redis fora do ar e reconecta sozinha quando ele voltar.
var redis = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException(
        "Defina a conexão com o Redis em ConnectionStrings:Redis (variável ConnectionStrings__Redis)."));
redis.AbortOnConnectFail = false;
redis.ConnectTimeout = 2000;
// Um Redis lento não pode segurar o clique: em 500 ms desiste e vai ao banco
redis.AsyncTimeout = 500;
redis.SyncTimeout = 500;
// Com a conexão caída, falha na hora. O padrão guarda os comandos numa fila esperando o Redis
// voltar, e cada clique esperava ~1 s por operação antes de desistir.
redis.BacklogPolicy = BacklogPolicy.FailFast;
builder.Services.AddSingleton<IConnectionMultiplexer>(servicos =>
{
    var log = servicos.GetRequiredService<ILogger<CacheDeLinks>>();
    var conexao = ConnectionMultiplexer.Connect(redis);
    // Um aviso quando o Redis cai e outro quando volta (e não um a cada clique)
    conexao.ConnectionFailed += (_, e) =>
        log.LogWarning("Conexão com o Redis caiu ({Tipo}); os links vão direto ao banco", e.FailureType);
    conexao.ConnectionRestored += (_, _) => log.LogInformation("Conexão com o Redis voltou");
    return conexao;
});
builder.Services.AddSingleton<CacheDeLinks>();

// RabbitMQ (fila dos cliques). Sem a conexão configurada a API não sobe; com o RabbitMQ fora do ar
// ela sobe sim: o PublicadorDeCliques tenta conectar em segundo plano e os cliques esperam na memória.
var rabbitmq = builder.Configuration.GetConnectionString("RabbitMQ")
    ?? throw new InvalidOperationException(
        "Defina a conexão com o RabbitMQ em ConnectionStrings:RabbitMQ (variável ConnectionStrings__RabbitMQ).");
builder.Services.AddSingleton(new RabbitMQ.Client.ConnectionFactory
{
    Uri = new Uri(rabbitmq),
    // Depois da primeira conexão, a biblioteca reconecta sozinha e recria canais, filas e ligações
    AutomaticRecoveryEnabled = true,
    TopologyRecoveryEnabled = true,
    NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
    // O padrão é 30 s: um endereço que não responde seguraria o publicador esse tempo todo
    RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
});
builder.Services.AddSingleton<FilaDeCliques>();
// Singleton e serviço em segundo plano ao mesmo tempo: o /saude precisa da mesma instância que roda
builder.Services.AddSingleton<PublicadorDeCliques>();
builder.Services.AddHostedService(servicos => servicos.GetRequiredService<PublicadorDeCliques>());

// /saude: "Healthy" com tudo no ar; "Degraded" (ainda 200) sem o Redis ou sem o RabbitMQ;
// "Unhealthy" (503) sem o banco
builder.Services.AddHealthChecks()
    .AddDbContextCheck<BeaconContexto>("banco")
    .AddCheck<SaudeDoRedis>("redis")
    .AddCheck<SaudeDoRabbitMQ>("rabbitmq");

// Erros no formato padrão "problem details" (RFC 9457), como o ProblemDetail do Vigil
builder.Services.AddProblemDetails();

// Descrição da API (OpenAPI) gerada a partir das rotas; a página para testar fica em /docs
builder.Services.AddOpenApi();

var app = builder.Build();

// Aplica as migrações pendentes ao subir (o papel do Flyway no Vigil)
using (var escopo = app.Services.CreateScope())
{
    escopo.ServiceProvider.GetRequiredService<BeaconContexto>().Database.Migrate();
}

// Erro inesperado vira um 500 em JSON, sem mostrar detalhes internos. Um pedido malformado
// (corpo vazio, JSON quebrado) vira 400: no ambiente Development o ASP.NET lança uma exceção
// para esses casos, e sem o seletor abaixo ela viraria 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = erro => erro is BadHttpRequestException ruim ? ruim.StatusCode : StatusCodes.Status500InternalServerError,
});
// Respostas de erro sem corpo (400, 404, 415...) também saem em problem details
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference("/docs", opcoes => opcoes.WithTitle("Beacon"));

app.MapHealthChecks("/saude");
app.MapearLinks();
app.MapearEstatisticas();
app.MapGet("/", () => Results.Ok(new
{
    nome = "Beacon",
    descricao = "Encurtador de links com estatísticas de cliques",
}));

app.Run();
