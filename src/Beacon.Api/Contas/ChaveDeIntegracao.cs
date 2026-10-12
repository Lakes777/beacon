using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Contas;

/// <summary>
/// Acesso de outro programa (o Pursuit) à API, sem cookie e sem a senha do painel: o pedido manda
/// "Authorization: Bearer &lt;chave&gt;", com a chave de Integracao:Chave. Sem chave configurada, o
/// esquema não reconhece ninguém e só o cookie vale. Trocar a chave no .env e recriar o contêiner
/// corta o acesso, sem mexer na senha. A chave só lê e cria links (ver PodeUsar): ela fica em outra
/// máquina, e se vazar não serve para apagar nem mudar nada.
/// </summary>
public class ChaveDeIntegracao(
    IOptionsMonitor<AuthenticationSchemeOptions> opcoes, ILoggerFactory log, UrlEncoder codificador,
    IConfiguration configuracao)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opcoes, log, codificador)
{
    public const string Esquema = "chave";
    public const string Nome = "integracao";

    /// <summary>Uma chave curta poderia ser adivinhada; openssl rand -hex 32 dá 64 caracteres.</summary>
    public const int TamanhoMinimo = 32;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var esperada = configuracao["Integracao:Chave"]?.Trim();
        var cabecalho = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(esperada) || !cabecalho.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // Sem chave configurada ou sem "Bearer": não é com este esquema (o cookie ainda pode valer)
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        if (!Iguais(cabecalho["Bearer ".Length..].Trim(), esperada))
        {
            return Task.FromResult(AuthenticateResult.Fail("Chave de integração inválida"));
        }
        var identidade = new ClaimsIdentity([new Claim(ClaimTypes.Name, Nome)], Esquema);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identidade), Esquema)));
    }

    /// <summary>
    /// O que quem entrou pela chave pode fazer: ler (GET) e criar links (POST /api/links). Editar,
    /// desativar e apagar ficam só com quem entra no painel. Com o cookie junto, vale o do painel.
    /// </summary>
    public static bool PodeUsar(ClaimsPrincipal usuario, HttpContext? http)
    {
        var autenticadas = usuario.Identities.Where(i => i.IsAuthenticated).ToList();
        if (autenticadas.Count == 0 || autenticadas.Any(i => i.AuthenticationType != Esquema))
        {
            return true;
        }
        var pedido = http?.Request;
        return pedido is not null && (HttpMethods.IsGet(pedido.Method)
            || (HttpMethods.IsPost(pedido.Method) && pedido.Path.Equals("/api/links", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Compara em tempo constante: um "==" para no primeiro caractere diferente, e medir quanto a
    /// resposta demora revelaria a chave aos poucos. Os hashes têm sempre o mesmo tamanho, então nem o
    /// tamanho da chave vaza.
    /// </summary>
    internal static bool Iguais(string recebida, string esperada) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(recebida)), SHA256.HashData(Encoding.UTF8.GetBytes(esperada)));

    /// <summary>Sem login: 401 com o esquema no WWW-Authenticate, como manda o HTTP.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Bearer");
        return Task.CompletedTask;
    }

    /// <summary>A API não sobe com uma chave fraca: melhor descobrir no deploy do que depois.</summary>
    public static void Conferir(IConfiguration configuracao)
    {
        var chave = configuracao["Integracao:Chave"];
        if (!string.IsNullOrEmpty(chave) && chave.Trim().Length < TamanhoMinimo)
        {
            throw new InvalidOperationException(
                $"Integracao:Chave precisa de pelo menos {TamanhoMinimo} caracteres (gere com openssl rand -hex 32).");
        }
    }
}
