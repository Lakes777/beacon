using System.Net;

namespace Beacon.Api.Tests;

public class SaudeTest(ApiDeTeste api)
{
    [Fact]
    public async Task SaudeRespondeQuandoOBancoEstaNoAr()
    {
        var resposta = await api.CreateClient().GetAsync("/saude", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("Healthy", await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InicioDizOQueEAApi()
    {
        var corpo = await api.CreateClient().GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("Beacon", corpo);
    }
}
