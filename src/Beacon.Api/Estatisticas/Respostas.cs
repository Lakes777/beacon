namespace Beacon.Api.Estatisticas;

// O que sai em GET /api/links/{codigo}/estatisticas. Todas as contas deixam os robôs de fora.

/// <param name="Total">Cliques de pessoas no período.</param>
/// <param name="Robos">Cliques de robôs (prévias de link, buscadores) que ficaram de fora das contas.</param>
/// <param name="PorDia">Um item por dia do período, no horário de Brasília, inclusive os dias sem cliques.</param>
/// <param name="Origens">O site de onde a pessoa veio; "direto" quando não veio de nenhum.</param>
public record EstatisticasResposta(
    string Codigo,
    int Total,
    int Robos,
    List<CliquesNoDia> PorDia,
    List<Contagem> Navegadores,
    List<Contagem> Sistemas,
    List<Contagem> Aparelhos,
    List<Contagem> Origens);

public record CliquesNoDia(DateOnly Dia, int Cliques);

public record Contagem(string Nome, int Cliques);

/// <summary>O que sai em GET /api/estatisticas: todos os links juntos (sem robôs).</summary>
/// <param name="PorLink">Todos os links, do mais clicado ao menos (empate: ordem alfabética), inclusive os sem cliques.</param>
public record ResumoResposta(int Total, int Robos, List<CliquesNoDia> PorDia, List<CliquesDoLink> PorLink);

public record CliquesDoLink(string Codigo, int Cliques);
