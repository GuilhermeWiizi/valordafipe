using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FipeDados.Infrastructure.Coleta;

/// <summary>Lock de sessão, liberado pelo PostgreSQL inclusive quando o processo morre. O contexto deve ser descartado pelo chamador.</summary>
public static class ColetaMensalLock
{
    public static async Task<bool> TentarAsync(FipeDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(hashtextextended('fipedados:coleta-mensal:' || current_schema(), 0))";
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public static async Task LiberarAsync(FipeDbContext db)
    {
        // Não devolver uma sessão com advisory lock ao pool.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(hashtextextended('fipedados:coleta-mensal:' || current_schema(), 0))";
        await command.ExecuteScalarAsync();
    }
}
