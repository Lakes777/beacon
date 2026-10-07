using Beacon.Api.Banco;
using Beacon.Api.Links;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api.Tests;

/// <summary>
/// O caminho do código repetido que só o banco percebe (dois pedidos ao mesmo tempo, ou o sorteio
/// caindo num código que já existe). Pela API, a consulta antes do INSERT quase sempre chega primeiro.
/// </summary>
public class SalvarTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private static string CodigoUnico() => "s-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<string> JaExistente()
    {
        var codigo = CodigoUnico();
        await using var escopo = api.Services.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        banco.Links.Add(new Link { Codigo = codigo, Destino = "https://exemplo.com/" });
        await banco.SaveChangesAsync(Cancelar);
        return codigo;
    }

    [Fact]
    public async Task SorteioRepetidoTentaOutroCodigo()
    {
        var repetido = await JaExistente();
        var novo = CodigoUnico();
        var sorteios = new Queue<string>([repetido, novo]);

        await using var escopo = api.Services.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        var link = await LinkRotas.Salvar(banco, "https://exemplo.com/", null, sorteios.Dequeue, Cancelar);

        Assert.Equal(novo, link!.Codigo);
        Assert.Equal(1, await banco.Links.CountAsync(l => l.Codigo == novo, Cancelar));
    }

    [Fact]
    public async Task CodigoEscolhidoQueOBancoRecusaDevolveNull()
    {
        var repetido = await JaExistente();

        await using var escopo = api.Services.CreateAsyncScope();
        var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
        var link = await LinkRotas.Salvar(banco, "https://exemplo.com/", repetido, () => "nao-usado", Cancelar);

        Assert.Null(link);
        // O link recusado saiu do contexto: o próximo SaveChanges não tenta inseri-lo de novo
        Assert.DoesNotContain(banco.ChangeTracker.Entries<Link>(), e => e.State == EntityState.Added);
    }
}
