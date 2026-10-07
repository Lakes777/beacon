using Beacon.Api.Links;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Banco;

/// <summary>
/// A ponte entre as classes e o Postgres (o "EntityManager" do JPA). Cada DbSet é uma tabela.
/// Os nomes no banco ficam em snake_case (criado_em), como no Vigil, pelo UseSnakeCaseNamingConvention.
/// </summary>
public class BeaconContexto(DbContextOptions<BeaconContexto> opcoes) : DbContext(opcoes)
{
    public DbSet<Link> Links => Set<Link>();

    protected override void OnModelCreating(ModelBuilder modelo)
    {
        modelo.Entity<Link>(link =>
        {
            link.ToTable("link");
            link.Property(l => l.Codigo).HasMaxLength(64);
            link.Property(l => l.Destino).HasMaxLength(2048);
            // Ativo NÃO tem padrão no banco: com padrão true, o EF deixaria de mandar um false
            // (o valor padrão do bool no C#) e o banco gravaria true. O "= true" fica só na classe.
            link.Property(l => l.CriadoEm).HasDefaultValueSql("now()");
            // Dois links com o mesmo código: o banco recusa, mesmo se dois pedidos chegarem juntos
            link.HasIndex(l => l.Codigo).IsUnique();
        });
    }
}
