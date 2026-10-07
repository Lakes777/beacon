using Beacon.Estatisticas;

// O serviço de estatísticas: um programa à parte, sem rotas HTTP, que só consome a fila de cliques
var builder = Host.CreateApplicationBuilder(args);
builder.AdicionarEstatisticas();
builder.Build().Run();
