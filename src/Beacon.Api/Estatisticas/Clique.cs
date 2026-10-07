namespace Beacon.Api.Estatisticas;

/// <summary>
/// Um clique já processado. Quem grava é o serviço de estatísticas (Beacon.Estatisticas, com SQL
/// direto); a API só lê, para montar os números. A tabela é criada pelas migrações da API.
/// </summary>
public class Clique
{
    /// <summary>O Id da mensagem (CliqueRegistrado.Id): chave primária, por isso a mesma mensagem não entra duas vezes.</summary>
    public Guid Id { get; set; }

    /// <summary>Sem chave estrangeira para link: apagar um link apaga os cliques dele explicitamente.</summary>
    public required string Codigo { get; set; }

    public DateTimeOffset Momento { get; set; }

    /// <summary>Ex.: "Chrome", "Firefox", "Safari"; "Outro" se não deu para saber.</summary>
    public required string Navegador { get; set; }

    /// <summary>Ex.: "Windows", "Android", "iOS"; "Outro" se não deu para saber.</summary>
    public required string Sistema { get; set; }

    /// <summary>"computador", "celular", "tablet" ou "outro".</summary>
    public required string Aparelho { get; set; }

    /// <summary>O site de onde veio (só o domínio, ex.: "www.linkedin.com"); null = acesso direto.</summary>
    public string? Origem { get; set; }

    /// <summary>Prévias de link (LinkedIn, WhatsApp...) e robôs de busca. Guardado, mas fora das contas.</summary>
    public bool Robo { get; set; }
}
