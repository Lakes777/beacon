using MyCSharp.HttpUserAgentParser;

namespace Beacon.Estatisticas;

/// <summary>O que dá para saber de um clique a partir dos cabeçalhos (já cortado no tamanho das colunas).</summary>
public record Classificacao(string Navegador, string Sistema, string Aparelho, bool Robo);

/// <summary>
/// Transforma o User-Agent e o Referer em colunas da tabela clique. Sem I/O: só texto entra e sai,
/// por isso é testado sem banco nem fila.
/// </summary>
public static class Classificador
{
    // Os tamanhos das colunas (migração Cliques da API). Cortar aqui evita que um cabeçalho
    // estranho faça o INSERT falhar e a mensagem volte para a fila para sempre.
    public const int TamanhoDoCodigo = 64;
    public const int TamanhoDoNavegador = 64;
    public const int TamanhoDoSistema = 64;
    public const int TamanhoDoAparelho = 16;
    public const int TamanhoDaOrigem = 255;

    public const string Desconhecido = "Outro";

    /// <summary>
    /// Prévias de link e robôs que a biblioteca não conhece (ou chama por outro nome), com o nome
    /// que fica gravado. A ordem importa: o primeiro que aparecer no User-Agent ganha.
    /// Procurar só "bot" pegaria celulares da marca Cubot, por isso a lista é explícita.
    /// </summary>
    private static readonly (string Trecho, string Nome)[] Robos =
    [
        ("linkedinbot", "LinkedInBot"),
        ("whatsapp", "WhatsApp"),
        ("facebookexternalhit", "Facebook"),
        ("facebookcatalog", "Facebook"),
        ("meta-externalagent", "Facebook"),
        // Antes do Twitterbot: o Telegram se anuncia como "TelegramBot (like TwitterBot)"
        ("telegrambot", "TelegramBot"),
        ("twitterbot", "Twitterbot"),
        ("slackbot", "Slackbot"),
        ("slack-imgproxy", "Slackbot"),
        ("discordbot", "Discordbot"),
        ("skypeuripreview", "Skype"),
        ("googlebot", "Googlebot"),
        ("google-inspectiontool", "Googlebot"),
        ("googleother", "Googlebot"),
        ("adsbot-google", "Googlebot"),
        ("bingbot", "bingbot"),
        ("bingpreview", "bingbot"),
        ("applebot", "Applebot"),
        ("duckduckbot", "DuckDuckBot"),
        ("yandexbot", "YandexBot"),
        ("baiduspider", "Baiduspider"),
        ("pinterestbot", "Pinterestbot"),
        ("redditbot", "redditbot"),
        ("embedly", "Embedly"),
        ("mastodon", "Mastodon"),
        ("gptbot", "GPTBot"),
        ("chatgpt-user", "ChatGPT"),
        ("claudebot", "ClaudeBot"),
        ("ahrefsbot", "AhrefsBot"),
        ("semrushbot", "SemrushBot"),
        ("petalbot", "PetalBot"),
        ("bytespider", "Bytespider"),
        ("headlesschrome", "HeadlessChrome"),
        ("curl/", "curl"),
        ("wget/", "Wget"),
        ("python-requests", "python-requests"),
        ("go-http-client", "Go-http-client"),
        ("okhttp", "okhttp"),
    ];

    // Palavras genéricas de robô, para os que não estão na lista (ex.: "AlgumCrawler/1.0")
    private static readonly string[] SinaisDeRobo = ["crawler", "spider", "bot/", "bot;", "+http"];

    public static Classificacao Classificar(string? userAgent)
    {
        // Todo navegador manda User-Agent: sem ele, é um script ou um robô
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new(Desconhecido, Desconhecido, "outro", Robo: true);
        }

        var robo = NomeDoRobo(userAgent);
        if (robo is not null)
        {
            return new(Cortar(robo, TamanhoDoNavegador), Desconhecido, "outro", Robo: true);
        }

        var info = HttpUserAgentParser.Parse(userAgent);
        if (info.IsRobot())
        {
            return new(Cortar(info.Name ?? Desconhecido, TamanhoDoNavegador), Desconhecido, "outro", Robo: true);
        }

        var sistema = Sistema(info.Platform?.PlatformType);
        return new(Navegador(info.Name), sistema, Aparelho(userAgent, sistema), Robo: false);
    }

    /// <summary>Só o domínio de onde a pessoa veio (ex.: "www.linkedin.com"); null se não veio ou não é um endereço.</summary>
    public static string? Origem(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer)
            || !Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var endereco)
            || (endereco.Scheme != Uri.UriSchemeHttp && endereco.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(endereco.Host))
        {
            return null;
        }

        // IdnHost: "café.com.br" vira "xn--caf-dma.com.br", sempre ASCII e em minúsculas
        return Cortar(endereco.IdnHost.ToLowerInvariant(), TamanhoDaOrigem);
    }

    public static string Cortar(string texto, int tamanho) => texto.Length <= tamanho ? texto : texto[..tamanho];

    private static string? NomeDoRobo(string userAgent)
    {
        foreach (var (trecho, nome) in Robos)
        {
            if (userAgent.Contains(trecho, StringComparison.OrdinalIgnoreCase))
            {
                return nome;
            }
        }

        foreach (var sinal in SinaisDeRobo)
        {
            if (userAgent.Contains(sinal, StringComparison.OrdinalIgnoreCase))
            {
                return Desconhecido;
            }
        }

        return null;
    }

    // A biblioteca dá nomes como "Chrome" e "Firefox"; aqui só se junta variações num nome só
    private static string Navegador(string? nome) => nome switch
    {
        null or "" => Desconhecido,
        "Edge" or "Microsoft Edge" => "Edge",
        "Internet Explorer" or "IE" => "Internet Explorer",
        _ => Cortar(nome, TamanhoDoNavegador),
    };

    private static string Sistema(HttpUserAgentPlatformType? tipo) => tipo switch
    {
        HttpUserAgentPlatformType.Windows => "Windows",
        HttpUserAgentPlatformType.Android => "Android",
        HttpUserAgentPlatformType.IOS => "iOS",
        HttpUserAgentPlatformType.MacOS => "macOS",
        HttpUserAgentPlatformType.Linux => "Linux",
        HttpUserAgentPlatformType.ChromeOS => "ChromeOS",
        _ => Desconhecido,
    };

    private static string Aparelho(string userAgent, string sistema)
    {
        bool Tem(string trecho) => userAgent.Contains(trecho, StringComparison.OrdinalIgnoreCase);

        // Celular Android manda "Mobile"; tablet Android não manda
        if (Tem("ipad") || Tem("tablet") || (sistema == "Android" && !Tem("mobile")))
        {
            return "tablet";
        }

        if (Tem("iphone") || Tem("ipod") || Tem("mobile") || sistema is "Android" or "iOS")
        {
            return "celular";
        }

        return sistema is "Windows" or "macOS" or "Linux" or "ChromeOS" ? "computador" : "outro";
    }
}
