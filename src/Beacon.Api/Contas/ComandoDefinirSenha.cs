using System.Text;
using Beacon.Api.Banco;
using Microsoft.AspNetCore.Identity;

namespace Beacon.Api.Contas;

/// <summary>
/// dotnet run --project src/Beacon.Api -- definir-senha &lt;nome&gt;
/// Cria a conta do painel, ou troca a senha dela. Não há rota de cadastro: quem cria a conta é quem
/// tem acesso ao servidor. A senha é pedida no terminal (sem aparecer na tela) ou lida da entrada
/// padrão (echo "..." | dotnet run ...), e nunca vai nos argumentos, que ficam no histórico do shell.
/// </summary>
public static class ComandoDefinirSenha
{
    public const string Nome = "definir-senha";

    public const string Uso = $"Uso: dotnet run --project src/Beacon.Api -- {Nome} <nome> (a senha é pedida depois)";

    /// <summary>
    /// O nome da conta, se os argumentos pedem o comando; um problema, se pedem do jeito errado (sem o
    /// nome, ou com a senha junto); ou nada dos dois, e a API sobe normalmente.
    /// </summary>
    public static (string? Nome, string? Problema) Interpretar(string[] args) => args switch
    {
        [Nome, var nome] => (nome, null),
        [Nome, ..] => (null, Uso),
        _ => (null, null),
    };

    /// <returns>O código de saída do programa (0 = deu certo).</returns>
    public static async Task<int> Rodar(IServiceProvider servicos, string nome)
    {
        string? senha;
        if (Console.IsInputRedirected)
        {
            senha = Console.In.ReadLine();
        }
        else
        {
            senha = LerEscondido("Senha: ");
            if (senha != LerEscondido("Repita a senha: "))
            {
                Console.Error.WriteLine("As senhas não são iguais. Nada foi alterado.");
                return 1;
            }
        }

        using var escopo = servicos.CreateScope();
        var (criou, problema) = await RegrasDeConta.DefinirSenha(
            escopo.ServiceProvider.GetRequiredService<BeaconContexto>(),
            escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(),
            nome, senha ?? "");
        if (problema is not null)
        {
            Console.Error.WriteLine($"{problema} Nada foi alterado.");
            return 1;
        }
        Console.WriteLine(criou
            ? $"Conta \"{RegrasDeConta.Normalizar(nome)}\" criada."
            : $"Senha de \"{RegrasDeConta.Normalizar(nome)}\" trocada; as sessões abertas foram encerradas.");
        return 0;
    }

    private static string LerEscondido(string pergunta)
    {
        Console.Write(pergunta);
        var texto = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var tecla && tecla.Key != ConsoleKey.Enter)
        {
            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (texto.Length > 0)
                {
                    texto.Length--;
                }
            }
            else if (!char.IsControl(tecla.KeyChar))
            {
                texto.Append(tecla.KeyChar);
            }
        }
        Console.WriteLine();
        return texto.ToString();
    }
}
