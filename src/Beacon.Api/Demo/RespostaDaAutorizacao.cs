using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Beacon.Api.Demo;

/// <summary>
/// Quando a autorização recusa um pedido do visitante, responde 403 com o motivo em problem details.
/// O 403 padrão sai sem corpo e vira um "Forbidden" genérico: o painel não teria o que mostrar.
/// Os outros casos (sem login, a chave tentando apagar) seguem o caminho padrão.
/// </summary>
public class RespostaDaAutorizacao(IProblemDetailsService problemas) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler padrao = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext http, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // Aqui http.User já é quem o cookie identificou (o middleware de autorização o preenche antes)
        if (authorizeResult.Forbidden && ModoDemo.EhVisitante(http.User))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await problemas.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails =
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Somente leitura",
                    Detail = ModoDemo.Proibido,
                },
            });
            return;
        }
        await padrao.HandleAsync(next, http, policy, authorizeResult);
    }
}
