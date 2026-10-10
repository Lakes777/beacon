using Beacon.Api.Links;

namespace Beacon.Api.Tests;

/// <summary>Regras puras, sem banco nem API: rodam em milissegundos.</summary>
public class RegrasTest
{
    [Theory]
    [InlineData("abc")]
    [InlineData("curriculo-vaga-x")]
    [InlineData("linkedin2026")]
    public void CodigosValidos(string codigo) => Assert.Null(Codigos.Problema(codigo));

    [Theory]
    [InlineData("ab")]                    // curto demais
    [InlineData("-curriculo")]            // hífen na ponta
    [InlineData("curriculo-")]
    [InlineData("currículo")]             // acento
    [InlineData("vaga x")]                // espaço
    [InlineData("a/b")]                   // barra mudaria a rota
    public void CodigosInvalidos(string codigo) => Assert.NotNull(Codigos.Problema(codigo));

    [Fact]
    public void CodigoLongoDemais() => Assert.NotNull(Codigos.Problema(new string('a', Codigos.TamanhoMaximo + 1)));

    [Fact]
    public void NormalizarIgnoraCaixaEEspacos() => Assert.Equal("curriculo", Codigos.Normalizar("  Curriculo "));

    [Fact]
    public void CodigoGeradoEValidoESemCaracteresConfusos()
    {
        for (var i = 0; i < 500; i++)
        {
            var codigo = Codigos.Gerar();
            Assert.Equal(Codigos.TamanhoGerado, codigo.Length);
            Assert.Null(Codigos.Problema(codigo));
            Assert.DoesNotContain(codigo, c => c is 'l' or 'o' or '0' or '1');
        }
    }

    [Theory]
    [InlineData("https://lakes777.github.io")]
    [InlineData("http://exemplo.com/caminho?x=1#fim")]
    public void DestinosValidos(string destino) => Assert.Null(Destinos.Validar(destino).Problema);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("lakes777.github.io")]          // sem http(s)://
    [InlineData("javascript:alert(1)")]         // rodaria código no navegador de quem clica
    [InlineData("ftp://exemplo.com/arquivo")]
    [InlineData("https://")]
    [InlineData("https://usuario:senha@exemplo.com")]   // senha ficaria guardada e visível
    [InlineData("https://google.com@site-falso.com")]   // parece Google, vai para outro lugar
    public void DestinosInvalidos(string? destino) => Assert.NotNull(Destinos.Validar(destino).Problema);

    [Theory]
    [InlineData("https://pt.wikipedia.org/wiki/Programação", "https://pt.wikipedia.org/wiki/Programa%C3%A7%C3%A3o")]
    [InlineData("https://exemplo.com/a b", "https://exemplo.com/a%20b")]
    [InlineData("https://ação.com.br/", "https://xn--ao-siap.com.br/")]
    [InlineData("  https://lakes777.github.io  ", "https://lakes777.github.io/")]
    public void DestinoGravadoSoComASCII(string destino, string esperado)
    {
        var (normalizado, problema) = Destinos.Validar(destino);

        Assert.Null(problema);
        Assert.Equal(esperado, normalizado);
    }

    [Fact]
    public void QuebraDeLinhaNoDestinoNaoChegaAoCabecalho()
    {
        var (normalizado, _) = Destinos.Validar("https://exemplo.com/a\r\nSet-Cookie: x=1");

        Assert.NotNull(normalizado);
        Assert.DoesNotContain('\r', normalizado);
        Assert.DoesNotContain('\n', normalizado);
        Assert.All(normalizado, c => Assert.True(c < 128));
    }

    [Fact]
    public void TamanhoContaDepoisDosAcentosViraremCodigos()
    {
        // 400 "ç" cabem em 2048 caracteres, mas viram 400 x 6 = 2400 depois de codificados
        Assert.NotNull(Destinos.Validar("https://exemplo.com/" + new string('ç', 400)).Problema);
    }

    [Fact]
    public void QrCodeEmSvgPretoNoBrancoSemTamanhoFixo()
    {
        var svg = CodigoQr.Svg("https://beacon.exemplo.com/r/portfolio");

        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains("viewBox", svg);
        Assert.DoesNotContain("width=", svg.Split('>')[0]);   // quem decide o tamanho é a página
        Assert.Contains("#000000", svg);
        Assert.Contains("#ffffff", svg);
        Assert.DoesNotContain("<script", svg);
    }

    [Fact]
    public void QrCodeMudaComOEnderecoEEDeterministico()
    {
        var um = CodigoQr.Svg("https://beacon.exemplo.com/r/um");

        Assert.Equal(um, CodigoQr.Svg("https://beacon.exemplo.com/r/um"));
        Assert.NotEqual(um, CodigoQr.Svg("https://beacon.exemplo.com/r/dois"));
    }
}
