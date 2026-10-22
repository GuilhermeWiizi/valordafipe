using FipeDados.Core.Entities;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Coleta;
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
    public DbSet<PedidoPix> PedidosPix => Set<PedidoPix>();
    public DbSet<NotificacaoMercadoPago> NotificacoesMercadoPago => Set<NotificacaoMercadoPago>();
    public DbSet<ColetaMensalConfiguracao> ColetaMensalConfiguracoes => Set<ColetaMensalConfiguracao>();
    public DbSet<ColetaMensalExecucao> ColetasMensais => Set<ColetaMensalExecucao>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ColetaMensalConfiguracao>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });
        b.Entity<ColetaMensalExecucao>(e =>
        {
            e.HasKey(x => x.Competencia);
            e.Property(x => x.Estado).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.UltimoErro).HasMaxLength(1000);
            e.Property(x => x.JobId).HasMaxLength(100);
            e.HasOne<TabelaReferencia>().WithMany().HasForeignKey(x => x.TabelaReferenciaCodigo);
        });

        b.Entity<PedidoPix>(e =>
        {
            e.ToTable("PedidosPix", t =>
            {
                t.HasCheckConstraint("CK_PedidosPix_Valor", "\"Valor\" > 0");
                t.HasCheckConstraint("CK_PedidosPix_Meses", "\"Meses\" IN (1, 3, 6, 12)");
            });
            e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PlanoCodigo).HasMaxLength(20);
            e.Property(x => x.PlanoNome).HasMaxLength(30);
            e.Property(x => x.Referencia).HasMaxLength(25);
            e.Property(x => x.PixCopiaCola).HasMaxLength(2048);
            e.Property(x => x.Recebedor).HasMaxLength(100);
            e.Property(x => x.Valor).HasPrecision(14, 2);
            e.Property(x => x.ValorRecebido).HasPrecision(14, 2);
            e.Property(x => x.NomePagador).HasMaxLength(120);
            e.Property(x => x.TransacaoInformada).HasMaxLength(32);
            e.Property(x => x.TransacaoConfirmada).HasMaxLength(32);
            e.Property(x => x.AdministradorId).HasMaxLength(450);
            e.Property(x => x.MotivoRecusa).HasMaxLength(500);
            e.HasIndex(x => x.Referencia).IsUnique();
            e.HasIndex(x => x.TransacaoConfirmada).IsUnique();
            e.Property(x => x.VendedorId).HasMaxLength(30);
            e.Property(x => x.EmailPagador).HasMaxLength(256);
            e.Property(x => x.ChaveIdempotencia).HasMaxLength(36);
            e.Property(x => x.OrdemExternaId).HasMaxLength(100);
            e.Property(x => x.PagamentoExternoId).HasMaxLength(100);
            e.Property(x => x.EstadoExterno).HasMaxLength(120);
            e.Property(x => x.ErroSincronizacao).HasMaxLength(120);
            e.Property(x => x.MotivoRevisao).HasMaxLength(250);
            e.Property(x => x.ValorDevolvido).HasPrecision(14, 2);
            e.HasIndex(x => x.OrdemExternaId).IsUnique();
            e.HasIndex(x => x.PagamentoExternoId).IsUnique();
            e.HasIndex(x => new { x.UsuarioId, x.PlanoCodigo, x.Provedor, x.AmbienteProducao, x.VendedorId }).IsUnique()
                .HasFilter("\"Status\" IN (0, 1, 4)");
            e.HasIndex(x => x.ProximaSincronizacaoEm);
            e.HasIndex(x => new { x.Status, x.CriadoEm });
        });

        b.Entity<NotificacaoMercadoPago>(e =>
        {
            e.Property(x => x.Chave).HasMaxLength(64);
            e.Property(x => x.OrdemId).HasMaxLength(100);
            e.Property(x => x.VendedorId).HasMaxLength(30);
            e.Property(x => x.UltimoErro).HasMaxLength(120);
            e.HasIndex(x => x.Chave).IsUnique();
            e.HasIndex(x => new { x.ProcessadoEm, x.ProximaTentativaEm });
        });

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
            // Ranking e coleta mensal filtram por referência: o INCLUDE permite index-only scan.
            e.HasIndex(x => x.TabelaReferenciaCodigo).IncludeProperties(x => new { x.VeiculoId, x.Valor });
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
