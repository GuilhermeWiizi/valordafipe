using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FipeDados.Tests;

public class SlugMigrationTests
{
    [PostgresFact]
    public async Task Migration_ResolveColisaoEntreNomeESufixoGerado_PreservaLinhas()
    {
        var schema = "slug_migration_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
        // Identificador de schema gerado localmente com prefixo fixo e GUID hexadecimal.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            await db.Database.MigrateAsync("20260602003730_Initial");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Marcas" ("Id", "Tipo", "CodigoFipe", "Nome") VALUES (1, 1, 1, 'Teste');
                INSERT INTO "Modelos" ("Id", "MarcaId", "CodigoFipe", "Nome") VALUES
                    (1, 1, 1, 'Modelo'), (2, 1, 2, 'Modelo'), (3, 1, 3, 'Modelo-2'), (4, 1, 4, 'Modelo-2-2');
                """);
            await db.Database.MigrateAsync();
            var slugs = await db.Modelos.OrderBy(m => m.Id).Select(m => m.Slug).ToListAsync();
            Assert.Equal(4, slugs.Count);
            Assert.Equal(4, slugs.Distinct().Count());
            Assert.Equal("modelo", slugs[0]);
            Assert.Equal("modelo-2", slugs[1]);
            Assert.Equal("modelo-2-3", slugs[2]);
            Assert.Equal("modelo-2-2", slugs[3]);
        }
        finally { await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE"); }
#pragma warning restore EF1002
    }
}
