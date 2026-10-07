using Beacon.Api.Banco;
using Beacon.Api.Links;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api.Tests;

/// <summary>A tabela criada pela migração, testada no Postgres de verdade.</summary>
public class BancoTest(ApiDeTeste api)
{
    private CancellationToken Cancelar => TestContext.Current.CancellationToken;

    /// <summary>Um escopo por "pedido", como na API: fechar o escopo fecha o contexto junto.</summary>
    private AsyncServiceScope NovoEscopo() => api.Services.CreateAsyncScope();

    private static BeaconContexto Banco(AsyncServiceScope escopo) =>
        escopo.ServiceProvider.GetRequiredService<BeaconContexto>();

    private static string CodigoUnico() => "teste-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task OBancoPreencheADataDeCriacao()
    {
        var codigo = CodigoUnico();
        await using (var escopo = NovoEscopo())
        {
            var banco = Banco(escopo);
            banco.Links.Add(new Link { Codigo = codigo, Destino = "https://lakes777.github.io" });
            await banco.SaveChangesAsync(Cancelar);
        }

        await using var outro = NovoEscopo();
        var salvo = await Banco(outro).Links.SingleAsync(l => l.Codigo == codigo, Cancelar);
        Assert.True(salvo.Ativo);
        Assert.InRange(salvo.CriadoEm, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task UmLinkDesativadoContinuaDesativado()
    {
        // Pega o erro clássico: com um padrão true no banco, o EF não mandaria o false
        var codigo = CodigoUnico();
        await using (var escopo = NovoEscopo())
        {
            var banco = Banco(escopo);
            banco.Links.Add(new Link { Codigo = codigo, Destino = "https://exemplo.com", Ativo = false });
            await banco.SaveChangesAsync(Cancelar);
        }

        await using var outro = NovoEscopo();
        Assert.False((await Banco(outro).Links.SingleAsync(l => l.Codigo == codigo, Cancelar)).Ativo);
    }

    [Fact]
    public async Task DoisLinksNaoPodemTerOMesmoCodigo()
    {
        var codigo = CodigoUnico();
        await using var primeiro = NovoEscopo();
        Banco(primeiro).Links.Add(new Link { Codigo = codigo, Destino = "https://exemplo.com/1" });
        await Banco(primeiro).SaveChangesAsync(Cancelar);

        await using var segundo = NovoEscopo();
        Banco(segundo).Links.Add(new Link { Codigo = codigo, Destino = "https://exemplo.com/2" });
        await Assert.ThrowsAsync<DbUpdateException>(() => Banco(segundo).SaveChangesAsync(Cancelar));
    }
}
