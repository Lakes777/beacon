namespace Beacon.Api.Contas;

/// <summary>
/// Quem pode entrar no painel. O Beacon é de uma pessoa só, mas a conta fica numa tabela (e não
/// numa variável de ambiente) para a senha poder ser trocada sem publicar de novo.
/// </summary>
public class Usuario
{
    public long Id { get; set; }

    /// <summary>Sempre em minúsculas.</summary>
    public required string Nome { get; set; }

    /// <summary>Saída do PasswordHasher (PBKDF2 com sal); a senha em si nunca é guardada.</summary>
    public required string SenhaHash { get; set; }

    /// <summary>
    /// Muda a cada troca de senha. Vai dentro do cookie e é conferido a cada pedido: trocar a senha
    /// derruba as sessões abertas (o "security stamp" do ASP.NET Identity).
    /// </summary>
    public Guid Carimbo { get; set; }

    /// <summary>Preenchido pelo banco (now()) ao inserir.</summary>
    public DateTimeOffset CriadoEm { get; set; }
}
