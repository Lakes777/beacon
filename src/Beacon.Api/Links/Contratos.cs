namespace Beacon.Api.Links;

// O que entra e sai pela API (os "DTOs"). A entidade Link fica só entre a API e o banco.
// Os campos aceitam null porque o JSON pode vir sem eles: a validação diz o que falta.

/// <param name="Codigo">Opcional: sem ele, o Beacon gera um aleatório de 7 caracteres.</param>
public record NovoLink(string? Destino, string? Codigo);

/// <param name="Ativo">Opcional: omitido, mantém como está.</param>
public record EdicaoDeLink(string? Destino, bool? Ativo);

public record LinkResposta(string Codigo, string Destino, bool Ativo, DateTimeOffset CriadoEm, string UrlCurta);
