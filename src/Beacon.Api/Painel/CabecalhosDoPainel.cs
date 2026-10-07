using Microsoft.AspNetCore.StaticFiles;

namespace Beacon.Api.Painel;

/// <summary>Cabeçalhos de segurança dos arquivos do painel (wwwroot).</summary>
public static class CabecalhosDoPainel
{
    /// <summary>
    /// Content-Security-Policy: a página só carrega scripts, estilos, fontes e imagens do próprio Beacon
    /// e só conversa com a própria API. Se um destino malicioso cadastrado num link conseguisse injetar
    /// HTML na página, o navegador recusaria rodar script de fora ou mandar dados para outro lugar.
    /// </summary>
    public const string Csp = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static void Proteger(StaticFileResponseContext contexto)
    {
        var cabecalhos = contexto.Context.Response.Headers;
        cabecalhos.ContentSecurityPolicy = Csp;
        // O navegador não "adivinha" o tipo de um arquivo (um .txt rodando como script)
        cabecalhos.XContentTypeOptions = "nosniff";
        // Ninguém põe o painel dentro de um iframe de outro site (clickjacking)
        cabecalhos.XFrameOptions = "DENY";
        cabecalhos["Referrer-Policy"] = "same-origin";
        // "no-cache" não é "não guarde": o navegador guarda, mas pergunta antes de usar se mudou (ETag),
        // e a resposta é um 304 sem corpo. Com cache por tempo, uma versão nova do HTML poderia chegar
        // junto com o painel.js antigo, e a página quebraria até o cache vencer.
        cabecalhos.CacheControl = "no-cache";
    }
}
