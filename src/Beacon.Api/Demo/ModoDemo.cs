using System.Security.Claims;

namespace Beacon.Api.Demo;

/// <summary>
/// O modo demonstração: uma segunda instância do Beacon, com banco próprio e dados de exemplo, onde
/// qualquer pessoa entra como "visitante" sem senha. Ele só lê: um encurtador aberto ao público vira
/// ferramenta de golpe (alguém criaria /r/xyz para uma página falsa de banco no domínio do André).
/// Desligado (o padrão), nada muda: a rota do visitante dá 404 e um cookie de visitante não vale.
/// </summary>
public static class ModoDemo
{
    /// <summary>Variável Demo__Ativo. Só a instância de demonstração liga.</summary>
    public const string Configuracao = "Demo:Ativo";

    /// <summary>O nome que aparece no painel e em GET /api/sessao.</summary>
    public const string Visitante = "visitante";

    /// <summary>
    /// O claim que marca o visitante. Vai dentro do cookie, que é cifrado: ninguém fabrica um cookie
    /// com ele. Uma conta real chamada "visitante" não tem o claim, então continua sendo conta real.
    /// </summary>
    public const string ClaimDoPapel = "papel";

    public const string Proibido = "Na demonstração, os links não podem ser criados nem mudados.";

    public static bool Ativo(IConfiguration configuracao) => configuracao.GetValue(Configuracao, false);

    public const string DestinoProibido = "Na demonstração, os links só levam ao portfólio (andrelagos.dev) ou ao GitHub do André (github.com/Lakes777).";

    /// <summary>
    /// Na demonstração, um link só pode levar a um site do André, mesmo criado pela chave de integração
    /// (a demo do Pursuit): se a chave da demo vazasse, ainda assim ninguém faria um /r/ para um golpe.
    /// </summary>
    public static bool DestinoPermitido(string destino) =>
        Uri.TryCreate(destino, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && (uri.Host is "andrelagos.dev" or "lakes777.github.io"
            || (uri.Host == "github.com" && (uri.AbsolutePath == "/Lakes777" || uri.AbsolutePath.StartsWith("/Lakes777/", StringComparison.Ordinal))));

    /// <summary>
    /// A semeadura apaga tudo: além de Demo:Ativo, o banco precisa se chamar algo terminado em "_demo"
    /// (ex.: beacon_demo). Ligar a flag por engano no contêiner real não apaga o banco de verdade.
    /// </summary>
    public static bool BancoDeDemo(string? nomeDoBanco) =>
        nomeDoBanco is not null && nomeDoBanco.EndsWith("_demo", StringComparison.Ordinal);

    public static bool EhVisitante(ClaimsPrincipal usuario) => usuario.HasClaim(ClaimDoPapel, Visitante);

    /// <summary>
    /// O que o visitante pode fazer: só GET (a lista, os números, o QR). Sair (DELETE /api/sessao) é
    /// público e nem passa por aqui. Quem não é visitante não é limitado por esta regra.
    /// </summary>
    public static bool PodeUsar(ClaimsPrincipal usuario, HttpContext? http) =>
        !EhVisitante(usuario) || (http is not null && HttpMethods.IsGet(http.Request.Method));
}
