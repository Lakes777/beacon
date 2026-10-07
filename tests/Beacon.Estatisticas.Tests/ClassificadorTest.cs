namespace Beacon.Estatisticas.Tests;

/// <summary>User-Agents reais, copiados de navegadores e de prévias de link.</summary>
public class ClassificadorTest
{
    private const string ChromeNoWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";
    private const string SafariNoIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1";
    private const string ChromeNoAndroid =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36";
    private const string SafariNoIpad =
        "Mozilla/5.0 (iPad; CPU OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1";
    private const string ChromeNoTabletAndroid =
        "Mozilla/5.0 (Linux; Android 13; SM-X200) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";
    private const string FirefoxNoLinux =
        "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0";
    private const string SafariNoMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15";
    private const string EdgeNoWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 Edg/129.0.2792.79";
    private const string CelularCubot =
        "Mozilla/5.0 (Linux; Android 11; KingKong 7 Build/RP1A.200720.011; CUBOT) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36";

    [Theory]
    [InlineData(ChromeNoWindows, "Chrome", "Windows", "computador")]
    [InlineData(SafariNoIphone, "Safari", "iOS", "celular")]
    [InlineData(ChromeNoAndroid, "Chrome", "Android", "celular")]
    [InlineData(SafariNoIpad, "Safari", "iOS", "tablet")]
    [InlineData(ChromeNoTabletAndroid, "Chrome", "Android", "tablet")]
    [InlineData(FirefoxNoLinux, "Firefox", "Linux", "computador")]
    [InlineData(SafariNoMac, "Safari", "macOS", "computador")]
    [InlineData(EdgeNoWindows, "Edge", "Windows", "computador")]
    [InlineData(CelularCubot, "Chrome", "Android", "celular")]
    public void ReconheceNavegadorSistemaEAparelho(string userAgent, string navegador, string sistema, string aparelho)
    {
        Assert.Equal(new Classificacao(navegador, sistema, aparelho, Robo: false), Classificador.Classificar(userAgent));
    }

    [Theory]
    [InlineData("LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)", "LinkedInBot")]
    [InlineData("WhatsApp/2.23.20.0", "WhatsApp")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", "Facebook")]
    [InlineData("Twitterbot/1.0", "Twitterbot")]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)", "Slackbot")]
    [InlineData("TelegramBot (like TwitterBot)", "TelegramBot")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)", "Discordbot")]
    [InlineData("Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.6668.70 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Googlebot")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", "bingbot")]
    [InlineData("curl/8.5.0", "curl")]
    [InlineData("AlgumCrawler/1.0 (+https://exemplo.com/robo)", "Outro")]
    public void ReconhecePreviasDeLinkERobos(string userAgent, string nome)
    {
        Assert.Equal(new Classificacao(nome, "Outro", "outro", Robo: true), Classificador.Classificar(userAgent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UserAgentVazioEOutroERobo(string? userAgent)
    {
        // Todo navegador manda User-Agent: sem ele, quem clicou foi um script
        Assert.Equal(new Classificacao("Outro", "Outro", "outro", Robo: true), Classificador.Classificar(userAgent));
    }

    [Fact]
    public void UserAgentDesconhecidoViraOutro()
    {
        Assert.Equal(new Classificacao("Outro", "Outro", "outro", Robo: false), Classificador.Classificar("qualquer coisa"));
    }

    [Fact]
    public void CadaCampoCabeNaColuna()
    {
        var gigante = "Mozilla/5.0 (Windows NT 10.0) " + new string('x', 5000) + "bot/1.0";
        var classe = Classificador.Classificar(gigante);
        Assert.True(classe.Navegador.Length <= Classificador.TamanhoDoNavegador);
        Assert.True(classe.Sistema.Length <= Classificador.TamanhoDoSistema);
        Assert.True(classe.Aparelho.Length <= Classificador.TamanhoDoAparelho);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/feed/?trk=abc", "www.linkedin.com")]
    [InlineData("HTTPS://WWW.Google.COM.BR/search?q=beacon", "www.google.com.br")]
    [InlineData("http://localhost:5173/pagina", "localhost")]
    [InlineData("https://lakes777.github.io", "lakes777.github.io")]
    [InlineData("android-app://com.linkedin.android/", null)]
    [InlineData("não é um endereço", null)]
    [InlineData("/caminho/relativo", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OrigemEhSoODominio(string? referer, string? origem)
    {
        Assert.Equal(origem, Classificador.Origem(referer));
    }

    [Fact]
    public void OrigemCabeNaColuna()
    {
        var referer = "https://" + string.Join('.', Enumerable.Repeat(new string('a', 60), 6)) + "/";
        Assert.Equal(Classificador.TamanhoDaOrigem, Classificador.Origem(referer)!.Length);
    }
}
