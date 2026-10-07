using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Beacon.Api.Banco;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Contas;

/// <param name="Usuario">O nome da conta (maiúsculas e minúsculas tanto faz).</param>
public record Login(string? Usuario, string? Senha);

public record SessaoResposta(string Usuario);

/// <summary>
/// Login por cookie: POST /api/sessao confere a senha e devolve um cookie que o navegador manda
/// sozinho nos pedidos seguintes. Toda rota pede login, menos as marcadas com AllowAnonymous.
/// </summary>
public static class SessaoRotas
{
    public const string Cookie = "beacon_sessao";
    public const string PoliticaDeLogin = "login";
    private const string ClaimDoCarimbo = "carimbo";

    public static void AdicionarLogin(this IServiceCollection servicos, IConfiguration configuracao)
    {
        servicos.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();

        servicos.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(opcoes =>
            {
                opcoes.Cookie.Name = Cookie;
                // HttpOnly: um script na página não lê o cookie (um XSS não rouba a sessão)
                opcoes.Cookie.HttpOnly = true;
                // Strict: o navegador não manda o cookie em pedidos que partem de outro site. É a defesa
                // contra CSRF (um site malicioso apagando links com a sessão de quem está logado). Há uma
                // segunda camada: um formulário de outro site só envia GET e POST, e não em JSON; e um fetch
                // de outro site com JSON, PUT ou DELETE precisa da permissão do CORS, que a API não dá.
                opcoes.Cookie.SameSite = SameSiteMode.Strict;
                // Só em pedidos para a API: o cookie não viaja em cada clique de /r/
                opcoes.Cookie.Path = "/api";
                // Em https o cookie sai com Secure (atrás do proxy, depende do UseForwardedHeaders da fase 7)
                opcoes.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                opcoes.ExpireTimeSpan = TimeSpan.FromDays(7);
                // A cada uso depois da metade do prazo, o prazo recomeça: quem usa o painel não é deslogado
                opcoes.SlidingExpiration = true;
                // O padrão é redirecionar para uma página de login (sites com telas no servidor).
                // Numa API, o certo é responder 401 (sem login) e 403 (sem permissão).
                opcoes.Events.OnRedirectToLogin = contexto =>
                {
                    contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                opcoes.Events.OnRedirectToAccessDenied = contexto =>
                {
                    contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                opcoes.Events.OnValidatePrincipal = ConferirCarimbo;
            });

        // Seguro por padrão: uma rota nova já nasce pedindo login; as públicas dizem AllowAnonymous
        servicos.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // Contra quem tenta adivinhar a senha: poucas tentativas de login por minuto por endereço IP
        var tentativas = configuracao.GetValue("Login:TentativasPorMinuto", 10);
        servicos.AddRateLimiter(opcoes =>
        {
            opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opcoes.OnRejected = (contexto, _) =>
            {
                if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
                {
                    contexto.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(espera.TotalSeconds)).ToString();
                }
                return ValueTask.CompletedTask;
            };
            opcoes.AddPolicy(PoliticaDeLogin, http => RateLimitPartition.GetFixedWindowLimiter(
                ChaveDoLimite(http.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = tentativas,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
    }

    /// <summary>
    /// De quem é a tentativa de login. Em IPv4, o endereço. Em IPv6, os primeiros 64 bits: um servidor
    /// alugado costuma ganhar um bloco /64 inteiro (bilhões de endereços), e trocar de endereço a cada
    /// tentativa escaparia do limite.
    /// </summary>
    internal static string ChaveDoLimite(IPAddress? ip)
    {
        if (ip is null)
        {
            return "desconhecido";
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public static void MapearSessao(this IEndpointRouteBuilder app)
    {
        var sessao = app.MapGroup("/api/sessao").WithTags("Sessão");
        sessao.MapPost("/", Entrar).AllowAnonymous().RequireRateLimiting(PoliticaDeLogin)
            .WithSummary("Entra no painel: confere usuário e senha e devolve o cookie da sessão");
        sessao.MapGet("/", Atual).WithSummary("Quem está logado (401 se ninguém)");
        // Lambda, e não um método: um método que só recebe HttpContext seria tratado como um
        // RequestDelegate "cru", e o 204 que ele devolve seria descartado (aviso ASP0016)
        sessao.MapDelete("/", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return TypedResults.NoContent();
        }).AllowAnonymous().WithSummary("Sai do painel (apaga o cookie)");
    }

    private static async Task<Results<Ok<SessaoResposta>, ProblemHttpResult>> Entrar(
        Login pedido, BeaconContexto banco, IPasswordHasher<Usuario> hasher, HttpContext http,
        CancellationToken cancelar)
    {
        // Senha comprida demais nem passa pelo PBKDF2 (ver TamanhoMaximoDaSenha), e um nome de vários MB
        // não vai para a consulta ao banco: nenhuma conta tem nome ou senha desse tamanho
        var usuario = pedido.Usuario is null || pedido.Senha is null
            || pedido.Usuario.Trim().Length > RegrasDeConta.TamanhoMaximoDoNome
            || pedido.Senha.Length > RegrasDeConta.TamanhoMaximoDaSenha
            ? null
            : await RegrasDeConta.Conferir(banco, hasher, pedido.Usuario, pedido.Senha, cancelar);
        if (usuario is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Usuário ou senha incorretos");
        }
        var identidade = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
                new Claim(ClaimTypes.Name, usuario.Nome),
                new Claim(ClaimDoCarimbo, usuario.Carimbo.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(new ClaimsPrincipal(identidade), new AuthenticationProperties { IsPersistent = true });
        return TypedResults.Ok(new SessaoResposta(usuario.Nome));
    }

    private static Ok<SessaoResposta> Atual(ClaimsPrincipal usuario) =>
        TypedResults.Ok(new SessaoResposta(usuario.Identity!.Name!));

    /// <summary>
    /// A cada pedido com cookie: a conta ainda existe e o carimbo é o mesmo? Se a senha foi trocada
    /// (ou a conta apagada), o cookie antigo deixa de valer. Custa uma consulta pela chave primária.
    /// </summary>
    private static async Task ConferirCarimbo(CookieValidatePrincipalContext contexto)
    {
        var id = contexto.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var carimbo = contexto.Principal?.FindFirstValue(ClaimDoCarimbo);
        var banco = contexto.HttpContext.RequestServices.GetRequiredService<BeaconContexto>();
        var valeAinda = long.TryParse(id, out var numero) && Guid.TryParse(carimbo, out var guid)
            && await banco.Usuarios.AnyAsync(u => u.Id == numero && u.Carimbo == guid, contexto.HttpContext.RequestAborted);
        if (!valeAinda)
        {
            contexto.RejectPrincipal();
            await contexto.HttpContext.SignOutAsync();
        }
    }
}
