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

    public static bool EhVisitante(ClaimsPrincipal usuario) => usuario.HasClaim(ClaimDoPapel, Visitante);

    /// <summary>
    /// O que o visitante pode fazer: só GET (a lista, os números, o QR). Sair (DELETE /api/sessao) é
    /// público e nem passa por aqui. Quem não é visitante não é limitado por esta regra.
    /// </summary>
    public static bool PodeUsar(ClaimsPrincipal usuario, HttpContext? http) =>
        !EhVisitante(usuario) || (http is not null && HttpMethods.IsGet(http.Request.Method));
}
