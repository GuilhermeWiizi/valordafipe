using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FipeDados.Infrastructure.Data;

/// <summary>
/// Factory usada apenas pelas ferramentas de migração do EF (design-time).
/// Lê a connection string de FIPE_DB_CONNECTION ou usa o padrão de desenvolvimento
/// (mesmas credenciais do docker-compose). Não é usada em runtime.
/// </summary>
public class FipeDbContextFactory : IDesignTimeDbContextFactory<FipeDbContext>
{
    public FipeDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("FIPE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=fipedados;Username=fipe;Password=fipe";

        var options = new DbContextOptionsBuilder<FipeDbContext>()
            .UseNpgsql(conn)
            .Options;

        return new FipeDbContext(options);
    }
}
