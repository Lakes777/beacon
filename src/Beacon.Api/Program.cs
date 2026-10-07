using Beacon.Api.Banco;
using Beacon.Api.Links;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Melhor a API nem subir do que subir sem banco e falhar no primeiro pedido
var conexao = builder.Configuration.GetConnectionString("Banco")
    ?? throw new InvalidOperationException(
        "Defina a conexão com o banco em ConnectionStrings:Banco (variável ConnectionStrings__Banco).");

// Injeção de dependência: registra o contexto do banco, e quem precisar dele recebe pelo construtor
builder.Services.AddDbContext<BeaconContexto>(opcoes => opcoes
    .UseNpgsql(conexao)
    .UseSnakeCaseNamingConvention());

// /saude responde "Healthy" só se o banco também responder
builder.Services.AddHealthChecks().AddDbContextCheck<BeaconContexto>("banco");

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
app.MapGet("/", () => Results.Ok(new
{
    nome = "Beacon",
    descricao = "Encurtador de links com estatísticas de cliques",
}));

app.Run();
