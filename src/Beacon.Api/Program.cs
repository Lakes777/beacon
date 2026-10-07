using Beacon.Api.Banco;
using Microsoft.EntityFrameworkCore;

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

var app = builder.Build();

// Aplica as migrações pendentes ao subir (o papel do Flyway no Vigil)
using (var escopo = app.Services.CreateScope())
{
    escopo.ServiceProvider.GetRequiredService<BeaconContexto>().Database.Migrate();
}

app.MapHealthChecks("/saude");
app.MapGet("/", () => Results.Ok(new
{
    nome = "Beacon",
    descricao = "Encurtador de links com estatísticas de cliques",
}));

app.Run();
