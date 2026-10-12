using System.Security.Claims;
using Beacon.Api.Contas;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Beacon.Api.Demo;

public record DemoResposta(bool Demo);

/// <summary>As rotas do modo demonstração: saber se ele está ligado e entrar como visitante.</summary>
public static class DemoRotas
{
    public static void MapearDemo(this IEndpointRouteBuilder app)
    {
        // Sempre mapeadas, e a configuração é lida a cada pedido: com a demo desligada a rota do
        // visitante responde 404 por conta própria (uma rota não mapeada cairia na política de login e daria 401)
        app.MapGet("/api/demo", (IConfiguration configuracao) => TypedResults.Ok(new DemoResposta(ModoDemo.Ativo(configuracao))))
            .AllowAnonymous().WithTags("Demonstração")
            .WithSummary("Se esta instância é a demonstração (o painel mostra o botão do visitante)");
        // Mesmo limite do login: sem ele, um robô abriria milhares de sessões por minuto
        app.MapPost("/api/sessao/visitante", EntrarComoVisitante)
            .AllowAnonymous().RequireRateLimiting(SessaoRotas.PoliticaDeLogin).WithTags("Demonstração")
            .WithSummary("Na demonstração, entra sem senha como visitante (só lê); 404 fora dela");
    }

    private static async Task<Results<Ok<SessaoResposta>, NotFound>> EntrarComoVisitante(
        HttpContext http, IConfiguration configuracao)
    {
        if (!ModoDemo.Ativo(configuracao))
        {
            return TypedResults.NotFound();
        }
        // Sem linha na tabela usuario: o visitante não tem senha nem carimbo (ver SessaoRotas.ConferirCarimbo)
        var identidade = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, ModoDemo.Visitante),
                new Claim(ModoDemo.ClaimDoPapel, ModoDemo.Visitante),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(new ClaimsPrincipal(identidade), new AuthenticationProperties { IsPersistent = true });
        return TypedResults.Ok(new SessaoResposta(ModoDemo.Visitante, Visitante: true));
    }
}
