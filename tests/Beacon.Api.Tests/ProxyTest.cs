using System.Net;
using System.Net.Http.Json;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api.Tests;

/// <summary>
/// Atrás do Caddy: os cabeçalhos X-Forwarded-* só valem quando o pedido vem da rede do proxy
/// (Proxy:Rede). O TestServer não tem IP de origem, então um filtro de inicialização põe um.
/// </summary>
public class ProxyTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    /// <summary>Faz todo pedido parecer vir de "origem", antes de qualquer outro middleware.</summary>
    private sealed class VindoDe(IPAddress origem) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> proximo) => app =>
        {
            app.Use((contexto, seguir) =>
            {
                contexto.Connection.RemoteIpAddress = origem;
                return seguir(contexto);
            });
            proximo(app);
        };
    }

    private async Task<LinkResposta> CriarVindoDe(string origem)
    {
        await using var fabrica = api.WithWebHostBuilder(b => b
            .UseSetting("Proxy:Rede", "172.30.0.0/24")
            .ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new VindoDe(IPAddress.Parse(origem)))));
        var cliente = await ApiDeTeste.Entrar(fabrica);
        var pedido = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new NovoLink("https://exemplo.com/", null)),
        };
        pedido.Headers.Add("X-Forwarded-Proto", "https");
        pedido.Headers.Add("X-Forwarded-For", "200.1.2.3");
        var resposta = await cliente.SendAsync(pedido, Cancelar);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<LinkResposta>(Cancelar))!;
    }

    [Fact]
    public async Task VindoDoProxyOEnderecoCurtoUsaHttps()
    {
        var link = await CriarVindoDe("172.30.0.7");

        Assert.StartsWith("https://", link.UrlCurta);
    }

    [Fact]
    public async Task ForaDaRedeDoProxyOsCabecalhosSaoIgnorados()
    {
        // Alguém mandando X-Forwarded-Proto direto para a API não engana a urlCurta (nem o limite de login)
        var link = await CriarVindoDe("10.9.9.9");

        Assert.StartsWith("http://", link.UrlCurta);
    }
}
