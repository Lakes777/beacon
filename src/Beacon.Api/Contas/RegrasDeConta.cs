using System.Text.RegularExpressions;
using Beacon.Api.Banco;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Contas;

/// <summary>Regras da conta do painel: o nome, a senha, criar ou trocar a senha e conferir o login.</summary>
public static partial class RegrasDeConta
{
    public const int TamanhoMinimoDoNome = 3;
    public const int TamanhoMaximoDoNome = 32;
    public const int TamanhoMinimoDaSenha = 12;
    // O PBKDF2 aceita qualquer tamanho, mas calcula sobre a senha inteira: uma "senha" de 1 MB
    // mandada ao /api/sessao várias vezes ocuparia o processador à toa
    public const int TamanhoMaximoDaSenha = 128;

    [GeneratedRegex("^[a-z0-9._-]+$")]
    private static partial Regex FormatoDoNome();

    /// <summary>
    /// Um hash qualquer, para conferir quando o usuário não existe. Sem ele, "usuário errado" responderia
    /// na hora e "senha errada" levaria o tempo do PBKDF2: pelo tempo dava para descobrir os nomes que existem.
    /// </summary>
    private static readonly string HashDeMentira = new PasswordHasher<Usuario>().HashPassword(null!, "beacon");

    public static string Normalizar(string nome) => nome.Trim().ToLowerInvariant();

    /// <summary>null se o nome (já normalizado) serve; senão, o motivo.</summary>
    public static string? ProblemaNoNome(string nome) => nome.Length switch
    {
        < TamanhoMinimoDoNome => $"Use pelo menos {TamanhoMinimoDoNome} caracteres.",
        > TamanhoMaximoDoNome => $"Use no máximo {TamanhoMaximoDoNome} caracteres.",
        _ when !FormatoDoNome().IsMatch(nome) => "Use só letras sem acento, números, ponto, hífen e sublinhado.",
        _ => null,
    };

    /// <summary>null se a senha serve; senão, o motivo. Só o tamanho: é o que mais pesa contra adivinhação.</summary>
    public static string? ProblemaNaSenha(string senha) => senha.Length switch
    {
        < TamanhoMinimoDaSenha => $"Use pelo menos {TamanhoMinimoDaSenha} caracteres.",
        > TamanhoMaximoDaSenha => $"Use no máximo {TamanhoMaximoDaSenha} caracteres.",
        _ when string.IsNullOrWhiteSpace(senha) => "A senha não pode ser só espaços.",
        _ => null,
    };

    /// <summary>
    /// Cria a conta, ou troca a senha se ela já existe. Trocar a senha gera um carimbo novo, o que
    /// derruba as sessões abertas. Devolve se criou e o problema (se houver, nada é gravado).
    /// </summary>
    public static async Task<(bool Criou, string? Problema)> DefinirSenha(BeaconContexto banco,
        IPasswordHasher<Usuario> hasher, string nome, string senha, CancellationToken cancelar = default)
    {
        var normalizado = Normalizar(nome);
        if (ProblemaNoNome(normalizado) is { } problemaNoNome)
        {
            return (false, $"Nome: {problemaNoNome}");
        }
        if (ProblemaNaSenha(senha) is { } problemaNaSenha)
        {
            return (false, $"Senha: {problemaNaSenha}");
        }
        var usuario = await banco.Usuarios.SingleOrDefaultAsync(u => u.Nome == normalizado, cancelar);
        var criou = usuario is null;
        usuario ??= banco.Usuarios.Add(new Usuario { Nome = normalizado, SenhaHash = "" }).Entity;
        usuario.SenhaHash = hasher.HashPassword(usuario, senha);
        usuario.Carimbo = Guid.NewGuid();
        await banco.SaveChangesAsync(cancelar);
        return (criou, null);
    }

    /// <summary>A conta, se o nome e a senha conferem; senão null (sem dizer qual dos dois errou).</summary>
    public static async Task<Usuario?> Conferir(BeaconContexto banco, IPasswordHasher<Usuario> hasher,
        string nome, string senha, CancellationToken cancelar = default)
    {
        var normalizado = Normalizar(nome);
        var usuario = await banco.Usuarios.SingleOrDefaultAsync(u => u.Nome == normalizado, cancelar);
        if (usuario is null)
        {
            hasher.VerifyHashedPassword(null!, HashDeMentira, senha);
            return null;
        }
        switch (hasher.VerifyHashedPassword(usuario, usuario.SenhaHash, senha))
        {
            case PasswordVerificationResult.Success:
                return usuario;
            case PasswordVerificationResult.SuccessRehashNeeded:
                // Hash de uma versão antiga do .NET (menos iterações): aproveita a senha certa para refazer.
                // O carimbo fica: a senha é a mesma, as sessões continuam valendo.
                usuario.SenhaHash = hasher.HashPassword(usuario, senha);
                await banco.SaveChangesAsync(cancelar);
                return usuario;
            default:
                return null;
        }
    }
}
