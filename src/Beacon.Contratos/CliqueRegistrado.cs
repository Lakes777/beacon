using System.Text.Json;

namespace Beacon.Contratos;

/// <summary>
/// A mensagem que a API publica na fila a cada clique em /r/{codigo}, e que o serviço de
/// estatísticas consome. É o "contrato" entre os dois programas: mudar um campo aqui muda os dois.
/// </summary>
/// <param name="Id">Gerado pela API, um por clique. O consumidor grava com ele como chave primária:
/// se a mesma mensagem chegar duas vezes (o RabbitMQ entrega "pelo menos uma vez"), o segundo INSERT
/// não faz nada, e o clique não conta em dobro.</param>
/// <param name="Codigo">O código já normalizado (minúsculas).</param>
/// <param name="Momento">Quando o clique aconteceu (na API), e não quando foi processado.</param>
/// <param name="UserAgent">O cabeçalho User-Agent: de onde saem navegador, sistema e aparelho.</param>
/// <param name="Referer">O cabeçalho Referer: o site de onde a pessoa veio (pode não vir).</param>
public record CliqueRegistrado(Guid Id, string Codigo, DateTimeOffset Momento, string? UserAgent, string? Referer)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] ParaBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>null se o conteúdo não for um clique válido (mensagem "envenenada").</summary>
    public static CliqueRegistrado? DeBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var clique = JsonSerializer.Deserialize<CliqueRegistrado>(bytes, Json);
            return clique is { Id: var id, Codigo.Length: > 0 } && id != Guid.Empty ? clique : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
