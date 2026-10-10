using System.Net;
using System.Net.Http.Json;
using Beacon.Api.Banco;
using Beacon.Api.Contas;
using Beacon.Api.Links;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Tests;

/// <summary>O login do painel (/api/sessao) e quais rotas pedem login.</summary>
public class SessaoTest(ApiDeTeste api)
{
    private static CancellationToken Cancelar => TestContext.Current.CancellationToken;

    private const string SenhaBoa = "uma senha bem comprida";

    /// <summary>Sem login, sem seguir redirecionamentos (para ver o 302 do /r/).</summary>
    private readonly HttpClient anonimo = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Guarda e devolve os cookies, como um navegador.</summary>
    private HttpClient Navegador() => api.CreateClient();

    /// <summary>Uma conta só deste teste (os testes rodam em paralelo no mesmo banco).</summary>
    private async Task<string> NovaConta(string senha = SenhaBoa)
    {
        var nome = "u-" + Guid.NewGuid().ToString("N")[..10];
        var (criou, problema) = await DefinirSenha(nome, senha);
        Assert.True(criou);
        Assert.Null(problema);
        return nome;
    }

    private async Task<(bool Criou, string? Problema)> DefinirSenha(string nome, string senha)
    {
        await using var escopo = api.Services.CreateAsyncScope();
        return await RegrasDeConta.DefinirSenha(escopo.ServiceProvider.GetRequiredService<BeaconContexto>(),
            escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(), nome, senha, Cancelar);
    }

    private static Task<HttpResponseMessage> Entrar(HttpClient cliente, string? usuario, string? senha) =>
        cliente.PostAsJsonAsync("/api/sessao", new Login(usuario, senha), Cancelar);

    [Fact]
    public async Task SemLoginAsRotasDaApiDao401()
    {
        var pedidos = new[]
        {
            anonimo.GetAsync("/api/links", Cancelar),
            anonimo.PostAsJsonAsync("/api/links", new NovoLink("https://exemplo.com", null), Cancelar),
            anonimo.GetAsync("/api/links/qualquer", Cancelar),
            anonimo.PutAsJsonAsync("/api/links/qualquer", new EdicaoDeLink("https://exemplo.com", null), Cancelar),
            anonimo.DeleteAsync("/api/links/qualquer", Cancelar),
            anonimo.GetAsync("/api/links/qualquer/estatisticas", Cancelar),
            anonimo.GetAsync("/api/links/qualquer/qr", Cancelar),
            anonimo.GetAsync("/api/sessao", Cancelar),
        };
        foreach (var resposta in await Task.WhenAll(pedidos))
        {
            // 401, e não o redirecionamento para uma página de login (o padrão do cookie)
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
            Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task RotasPublicasNaoPedemLogin()
    {
        var link = await (await api.ClienteLogado().PostAsJsonAsync("/api/links",
            new NovoLink("https://exemplo.com/publico", null), Cancelar)).Content.ReadFromJsonAsync<LinkResposta>(Cancelar);

        var redirecionamento = await anonimo.GetAsync($"/r/{link!.Codigo}", Cancelar);

        Assert.Equal(HttpStatusCode.Redirect, redirecionamento.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonimo.GetAsync("/r/nao-existe-mesmo", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonimo.GetAsync("/", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonimo.GetAsync("/saude", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonimo.GetAsync("/openapi/v1.json", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/docs", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task LoginCertoDevolveUmCookieProtegido()
    {
        var nome = await NovaConta();
        var navegador = Navegador();

        var resposta = await Entrar(navegador, nome, SenhaBoa);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(nome, (await resposta.Content.ReadFromJsonAsync<SessaoResposta>(Cancelar))!.Usuario);
        var cookie = resposta.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith($"{SessaoRotas.Cookie}=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api", cookie);
        Assert.Contains("expires=", cookie);    // continua logado depois de fechar o navegador
        // Com o cookie, a API responde
        var sessao = await navegador.GetFromJsonAsync<SessaoResposta>("/api/sessao", Cancelar);
        Assert.Equal(nome, sessao!.Usuario);
        Assert.Equal(HttpStatusCode.OK, (await navegador.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task NomeComMaiusculasEEspacosEntraNaMesmaConta()
    {
        var nome = await NovaConta();

        var resposta = await Entrar(Navegador(), $"  {nome.ToUpperInvariant()} ", SenhaBoa);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task SenhaErradaEUsuarioQueNaoExisteRespondemIgual()
    {
        var nome = await NovaConta();

        var senhaErrada = await Entrar(anonimo, nome, SenhaBoa + "x");
        var semConta = await Entrar(anonimo, "ninguem-" + Guid.NewGuid().ToString("N")[..8], SenhaBoa);

        // A mesma resposta nos dois casos: quem tenta adivinhar não descobre quais nomes existem
        Assert.Equal(HttpStatusCode.Unauthorized, senhaErrada.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, semConta.StatusCode);
        // (o corpo inteiro não: o traceId muda a cada pedido)
        var titulo = await Titulo(semConta);
        Assert.Equal("Usuário ou senha incorretos", titulo);
        Assert.Equal(titulo, await Titulo(senhaErrada));
        Assert.False(senhaErrada.Headers.Contains("Set-Cookie"));
    }

    private static async Task<string?> Titulo(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancelar)).GetProperty("title").GetString();

    [Theory]
    [InlineData(null, SenhaBoa)]
    [InlineData("teste", null)]
    [InlineData("", "")]
    public async Task LoginIncompletoDa401(string? usuario, string? senha)
    {
        var resposta = await Entrar(anonimo, usuario, senha);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    /// <summary>Um "hasher" que avisa se foi usado.</summary>
    private sealed class HasherVigiado : IPasswordHasher<Usuario>
    {
        private readonly PasswordHasher<Usuario> verdadeiro = new();
        public int Conferencias;

        public string HashPassword(Usuario usuario, string senha) => verdadeiro.HashPassword(usuario, senha);

        public PasswordVerificationResult VerifyHashedPassword(Usuario usuario, string hash, string senha)
        {
            Interlocked.Increment(ref Conferencias);
            return verdadeiro.VerifyHashedPassword(usuario, hash, senha);
        }
    }

    [Fact]
    public async Task NomeOuSenhaGigantesNemSaoConferidos()
    {
        var vigiado = new HasherVigiado();
        await using var comVigia = api.WithWebHostBuilder(b => b.ConfigureTestServices(servicos =>
            servicos.AddSingleton<IPasswordHasher<Usuario>>(vigiado)));
        var cliente = comVigia.CreateClient();

        var senhaGigante = await Entrar(cliente, ApiDeTeste.Usuario, new string('a', 100_000));
        var nomeGigante = await Entrar(cliente, new string('a', 100_000), ApiDeTeste.Senha);
        var normal = await Entrar(cliente, ApiDeTeste.Usuario, ApiDeTeste.Senha);

        Assert.Equal(HttpStatusCode.Unauthorized, senhaGigante.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, nomeGigante.StatusCode);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        // Só o login normal chegou ao PBKDF2
        Assert.Equal(1, vigiado.Conferencias);
    }

    [Fact]
    public async Task HashAntigoEhRefeitoNoLoginSemDerrubarSessoes()
    {
        var nome = "v2-" + Guid.NewGuid().ToString("N")[..10];
        // Um hash no formato antigo (Identity V2: PBKDF2 com SHA-1 e 1.000 iterações)
        var antigo = new PasswordHasher<Usuario>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        }));
        var carimbo = Guid.NewGuid();
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            var banco = escopo.ServiceProvider.GetRequiredService<BeaconContexto>();
            var usuario = new Usuario { Nome = nome, SenhaHash = "", Carimbo = carimbo };
            usuario.SenhaHash = antigo.HashPassword(usuario, SenhaBoa);
            banco.Usuarios.Add(usuario);
            await banco.SaveChangesAsync(Cancelar);
        }

        var resposta = await Entrar(anonimo, nome, SenhaBoa);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            var depois = await escopo.ServiceProvider.GetRequiredService<BeaconContexto>().Usuarios
                .AsNoTracking().SingleAsync(u => u.Nome == nome, Cancelar);
            // O primeiro byte diz o formato: 0x00 = V2, 0x01 = V3 (o atual)
            Assert.Equal(0x01, Convert.FromBase64String(depois.SenhaHash)[0]);
            Assert.Equal(carimbo, depois.Carimbo);
        }
        Assert.Equal(HttpStatusCode.OK, (await Entrar(anonimo, nome, SenhaBoa)).StatusCode);
    }

    [Theory]
    [InlineData("200.1.2.3", "200.1.2.3")]
    [InlineData("::ffff:200.1.2.3", "200.1.2.3")]
    [InlineData("2001:db8:aaaa:bbbb:1:2:3:4", "2001:db8:aaaa:bbbb::/64")]
    [InlineData("2001:db8:aaaa:bbbb:ffff:ffff:ffff:ffff", "2001:db8:aaaa:bbbb::/64")]
    [InlineData("2001:db8:aaaa:cccc::1", "2001:db8:aaaa:cccc::/64")]
    public void LimiteDeLoginAgrupaIPv6PorBloco(string ip, string chave) =>
        Assert.Equal(chave, SessaoRotas.ChaveDoLimite(IPAddress.Parse(ip)));

    [Fact]
    public void LimiteDeLoginSemIPUsaUmaChaveSo() =>
        Assert.Equal("desconhecido", SessaoRotas.ChaveDoLimite(null));

    [Theory]
    [InlineData(new string[0], null, null)]
    [InlineData(new[] { "--urls", "http://localhost:1" }, null, null)]
    [InlineData(new[] { "definir-senha", "andre" }, "andre", null)]
    [InlineData(new[] { "definir-senha" }, null, ComandoDefinirSenha.Uso)]
    [InlineData(new[] { "definir-senha", "andre", "minha-senha" }, null, ComandoDefinirSenha.Uso)]
    public void ArgumentosDoComandoDefinirSenha(string[] args, string? nome, string? problema) =>
        Assert.Equal((nome, problema), ComandoDefinirSenha.Interpretar(args));

    [Fact]
    public async Task SairEncerraASessao()
    {
        var nome = await NovaConta();
        var navegador = Navegador();
        await Entrar(navegador, nome, SenhaBoa);

        var saida = await navegador.DeleteAsync("/api/sessao", Cancelar);

        Assert.Equal(HttpStatusCode.NoContent, saida.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await navegador.GetAsync("/api/sessao", Cancelar)).StatusCode);
        // Sair sem estar logado também dá certo
        Assert.Equal(HttpStatusCode.NoContent, (await anonimo.DeleteAsync("/api/sessao", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task TrocarASenhaDerrubaAsSessoesAbertas()
    {
        var nome = await NovaConta();
        var navegador = Navegador();
        await Entrar(navegador, nome, SenhaBoa);

        var (criou, problema) = await DefinirSenha(nome, "outra senha comprida");

        Assert.False(criou);
        Assert.Null(problema);
        Assert.Equal(HttpStatusCode.Unauthorized, (await navegador.GetAsync("/api/sessao", Cancelar)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(anonimo, nome, SenhaBoa)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Entrar(anonimo, nome, "outra senha comprida")).StatusCode);
    }

    [Fact]
    public async Task ContaApagadaDerrubaAsSessoes()
    {
        var nome = await NovaConta();
        var navegador = Navegador();
        await Entrar(navegador, nome, SenhaBoa);

        await using (var escopo = api.Services.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<BeaconContexto>().Usuarios
                .Where(u => u.Nome == nome).ExecuteDeleteAsync(Cancelar);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await navegador.GetAsync("/api/sessao", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task CookieInventadoNaoEntra()
    {
        var cliente = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        cliente.DefaultRequestHeaders.Add("Cookie", $"{SessaoRotas.Cookie}=inventado");

        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/links", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task MuitasTentativasDeLoginSaoBarradas()
    {
        await using var comLimite = api.WithWebHostBuilder(b => b.UseSetting("Login:TentativasPorMinuto", "2"));
        var cliente = comLimite.CreateClient();

        var primeira = await Entrar(cliente, ApiDeTeste.Usuario, "errada 1");
        var segunda = await Entrar(cliente, ApiDeTeste.Usuario, "errada 2");
        var terceira = await Entrar(cliente, ApiDeTeste.Usuario, ApiDeTeste.Senha);

        Assert.Equal(HttpStatusCode.Unauthorized, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, segunda.StatusCode);
        // Nem a senha certa passa enquanto a janela não vira
        Assert.Equal(HttpStatusCode.TooManyRequests, terceira.StatusCode);
        Assert.True(int.Parse(terceira.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);
        // O limite é só do login: o resto da API não é afetado
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/saude", Cancelar)).StatusCode);
    }

    [Fact]
    public async Task DefinirSenhaRecusaNomeESenhaRuinsSemGravar()
    {
        var nome = "r-" + Guid.NewGuid().ToString("N")[..10];

        var (criouCurta, senhaCurta) = await DefinirSenha(nome, "curta");
        var (_, nomeRuim) = await DefinirSenha("joão silva", SenhaBoa);

        Assert.False(criouCurta);
        Assert.Equal($"Senha: Use pelo menos {RegrasDeConta.TamanhoMinimoDaSenha} caracteres.", senhaCurta);
        Assert.StartsWith("Nome: ", nomeRuim);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(anonimo, nome, "curta")).StatusCode);
        // Depois do erro, criar com uma senha boa funciona normalmente
        Assert.True((await DefinirSenha(nome, SenhaBoa)).Criou);
    }

    [Theory]
    [InlineData("ab", "Use pelo menos 3 caracteres.")]
    [InlineData("andre", null)]
    [InlineData("a.b_c-1", null)]
    [InlineData("joão", "Use só letras sem acento, números, ponto, hífen e sublinhado.")]
    [InlineData("com espaco", "Use só letras sem acento, números, ponto, hífen e sublinhado.")]
    public void RegrasDoNome(string nome, string? problema) =>
        Assert.Equal(problema, RegrasDeConta.ProblemaNoNome(nome));

    [Theory]
    [InlineData("12345678901", "Use pelo menos 12 caracteres.")]
    [InlineData("123456789012", null)]
    [InlineData("            ", "A senha não pode ser só espaços.")]
    public void RegrasDaSenha(string senha, string? problema) =>
        Assert.Equal(problema, RegrasDeConta.ProblemaNaSenha(senha));

    [Fact]
    public void SenhaDeMaisDe128CaracteresERecusada() =>
        Assert.Equal("Use no máximo 128 caracteres.", RegrasDeConta.ProblemaNaSenha(new string('a', 129)));
}
