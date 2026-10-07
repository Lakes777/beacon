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
