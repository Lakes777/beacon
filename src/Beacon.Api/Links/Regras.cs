using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Beacon.Api.Links;

/// <summary>Regras do código que vai no link curto (/r/{codigo}).</summary>
public static partial class Codigos
{
    public const int TamanhoGerado = 7;
    public const int TamanhoMinimo = 3;
    public const int TamanhoMaximo = 64;

    // Sem l, o, 0 e 1: num currículo impresso, "l" e "1" ou "o" e "0" se confundem.
    // 32 símbolos em 7 posições = 34 bilhões de códigos possíveis.
    private const string Alfabeto = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Letras minúsculas, números e hífen; começa e termina com letra ou número.</summary>
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex Formato();

    /// <summary>Aleatório de verdade (criptográfico): ninguém adivinha os links dos outros pela sequência.</summary>
    public static string Gerar() => RandomNumberGenerator.GetString(Alfabeto, TamanhoGerado);

    /// <summary>"Curriculo" e "curriculo" são o mesmo link: quem digita o endereço não erra pela caixa.</summary>
    public static string Normalizar(string codigo) => codigo.Trim().ToLowerInvariant();

    /// <summary>null se o código (já normalizado) serve; senão, o motivo.</summary>
    public static string? Problema(string codigo) => codigo.Length switch
    {
        < TamanhoMinimo => $"Use pelo menos {TamanhoMinimo} caracteres.",
        > TamanhoMaximo => $"Use no máximo {TamanhoMaximo} caracteres.",
        _ when !Formato().IsMatch(codigo) =>
            "Use só letras sem acento, números e hífen (o hífen não pode ficar na ponta).",
        _ => null,
    };
}

/// <summary>Regras do endereço para onde o link leva.</summary>
public static class Destinos
{
    public const int TamanhoMaximo = 2048;

    /// <summary>
    /// Confere o destino e devolve a forma que vai para o banco (ou o motivo de recusar).
    /// A forma gravada é só ASCII: acentos e espaços viram %C3%A7, %20...; domínio com acento vira
    /// "xn--...". Sem isso, o servidor recusaria o cabeçalho Location e o link daria erro a cada clique.
    /// </summary>
    public static (string? Destino, string? Problema) Validar(string? destino)
    {
        if (string.IsNullOrWhiteSpace(destino))
        {
            return (null, "Informe o endereço de destino.");
        }
        // Só http e https: um "javascript:..." viraria um link curto que roda código no navegador de quem clica
        if (!Uri.TryCreate(destino.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            return (null, "Use um endereço completo, começando com http:// ou https://.");
        }
        // "https://google.com@site-falso.com" parece Google e vai para outro lugar; e uma senha na URL
        // ficaria guardada e aparecendo na lista de links
        if (uri.UserInfo.Length > 0)
        {
            return (null, "O endereço não pode ter usuário ou senha (a parte antes do @).");
        }
        var normalizado = new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
        // Depois de normalizar: cada letra com acento vira 6 caracteres (%C3%A7)
        if (normalizado.Length > TamanhoMaximo)
        {
            return (null, $"Use no máximo {TamanhoMaximo} caracteres.");
        }
        return (normalizado, null);
    }
}
