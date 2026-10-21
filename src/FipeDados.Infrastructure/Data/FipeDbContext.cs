using FipeDados.Core.Entities;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Contas;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FipeDados.Infrastructure.Data;

/// <summary>
/// Catálogo e preços da Fipe, mais as tabelas do Identity (contas opcionais) e as chaves do
/// Data Protection, que precisam sobreviver a reinícios para manter logins e links de senha válidos.
/// </summary>
public class FipeDbContext : IdentityDbContext<Usuario>, IDataProtectionKeyContext
{
    public FipeDbContext(DbContextOptions<FipeDbContext> options) : base(options) { }

    public DbSet<TabelaReferencia> TabelasReferencia => Set<TabelaReferencia>();
    public DbSet<Marca> Marcas => Set<Marca>();
    public DbSet<Modelo> Modelos => Set<Modelo>();
    public DbSet<Veiculo> Veiculos => Set<Veiculo>();
    public DbSet<PrecoHistorico> Precos => Set<PrecoHistorico>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<TabelaReferencia>(e =>
        {
            e.HasKey(x => x.Codigo);
            e.Property(x => x.Codigo).ValueGeneratedNever();
            e.Property(x => x.Mes).HasMaxLength(40);
            e.HasIndex(x => x.Data);
        });

        b.Entity<Marca>(e =>
        {
            e.Property(x => x.Nome).HasMaxLength(120);
            e.Property(x => x.Slug).HasMaxLength(255);
            e.HasIndex(x => new { x.Tipo, x.CodigoFipe }).IsUnique();
            e.HasIndex(x => new { x.Tipo, x.Slug }).IsUnique();
            e.HasMany(x => x.Modelos).WithOne(x => x.Marca).HasForeignKey(x => x.MarcaId);
        });

        b.Entity<Modelo>(e =>
        {
            e.Property(x => x.Nome).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(255);
            e.HasIndex(x => new { x.MarcaId, x.CodigoFipe }).IsUnique();
            e.HasIndex(x => new { x.MarcaId, x.Slug }).IsUnique();
            e.HasMany(x => x.Veiculos).WithOne(x => x.Modelo).HasForeignKey(x => x.ModeloId);
        });

        b.Entity<Veiculo>(e =>
        {
            e.Property(x => x.AnoModeloCodigo).HasMaxLength(20);
            e.Property(x => x.AnoLabel).HasMaxLength(120);
            e.Property(x => x.CodigoFipe).HasMaxLength(20);
            e.HasIndex(x => new { x.ModeloId, x.AnoModeloCodigo }).IsUnique();
            e.HasIndex(x => x.CodigoFipe);
        });

        b.Entity<PrecoHistorico>(e =>
        {
            e.Property(x => x.Valor).HasPrecision(14, 2);
            e.HasIndex(x => new { x.VeiculoId, x.TabelaReferenciaCodigo }).IsUnique();
            e.HasOne(x => x.Veiculo).WithMany(x => x.Precos).HasForeignKey(x => x.VeiculoId);
            e.HasOne(x => x.TabelaReferencia).WithMany(x => x.Precos)
                .HasForeignKey(x => x.TabelaReferenciaCodigo);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AtribuirSlugsAsync(async: false, CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await AtribuirSlugsAsync(async: true, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Dá slug às marcas e modelos novos que chegam sem ele, evitando os já gravados e os
    /// da mesma gravação. Inclusões concorrentes do mesmo slug esbarram no índice único
    /// (DbUpdateException), tratado como conflito pelos chamadores.
    /// </summary>
    private async Task AtribuirSlugsAsync(bool async, CancellationToken ct)
    {
        var novos = ChangeTracker.Entries().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        foreach (var grupo in novos.OfType<Marca>().Where(m => m.Slug.Length == 0).GroupBy(m => m.Tipo))
        {
            var ocupados = await ListarAsync(Marcas.Where(m => m.Tipo == grupo.Key).Select(m => m.Slug), async, ct);
            ocupados.UnionWith(novos.OfType<Marca>().Where(m => m.Tipo == grupo.Key && m.Slug.Length > 0).Select(m => m.Slug));
            foreach (var marca in grupo) marca.Slug = Slugs.Unico(Slugs.Base(marca.Nome, marca.CodigoFipe), ocupados);
        }

        foreach (var grupo in novos.OfType<Modelo>().Where(m => m.Slug.Length == 0).GroupBy(ChaveMarca))
        {
            var ocupados = grupo.Key is int marcaId
                ? await ListarAsync(Modelos.Where(m => m.MarcaId == marcaId).Select(m => m.Slug), async, ct)
                : [];
            ocupados.UnionWith(novos.OfType<Modelo>().Where(m => m.Slug.Length > 0 && Equals(ChaveMarca(m), grupo.Key)).Select(m => m.Slug));
            foreach (var modelo in grupo) modelo.Slug = Slugs.Unico(Slugs.Base(modelo.Nome, modelo.CodigoFipe), ocupados);
        }
    }

    /// <summary>Marca ainda não gravada é identificada pela instância; as demais, pelo Id.</summary>
    private object? ChaveMarca(Modelo modelo)
        => modelo.Marca is { } marca && Entry(marca).State == EntityState.Added ? marca : modelo.MarcaId;

    private static async Task<HashSet<string>> ListarAsync(IQueryable<string> consulta, bool async, CancellationToken ct)
        => async ? (await consulta.ToListAsync(ct)).ToHashSet() : consulta.ToHashSet();
}
