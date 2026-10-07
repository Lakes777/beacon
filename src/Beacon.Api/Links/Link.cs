namespace Beacon.Api.Links;

/// <summary>
/// Um link curto: quem acessa /r/{Codigo} é mandado para o Destino.
/// </summary>
public class Link
{
    public long Id { get; set; }

    /// <summary>O pedaço que vai no endereço curto (ex.: "curriculo-vaga-x").</summary>
    public required string Codigo { get; set; }

    /// <summary>O endereço completo para onde o link leva.</summary>
    public required string Destino { get; set; }

    /// <summary>Link desativado deixa de redirecionar, mas guarda o histórico de cliques.</summary>
    public bool Ativo { get; set; } = true;

    /// <summary>Preenchido pelo banco (now()) ao inserir.</summary>
    public DateTimeOffset CriadoEm { get; set; }
}
