using QRCoder;

namespace Beacon.Api.Links;

/// <summary>QR code do link curto, em SVG: fica nítido em qualquer tamanho, impresso ou na tela.</summary>
public static class CodigoQr
{
    /// <summary>
    /// Preto no branco e com a margem branca em volta (quiet zone), mesmo no painel escuro: é o que
    /// a câmera do celular lê melhor. Nível M de correção aguenta ~15% do desenho sujo ou amassado.
    /// O viewBox (sem largura e altura fixas) deixa a página escolher o tamanho; ele sai em módulos
    /// (ex.: 0 0 37 37), então o 8 de pixels por módulo não muda nada aqui, só é exigido pela assinatura.
    /// </summary>
    public static string Svg(string texto)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(texto, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(dados).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
