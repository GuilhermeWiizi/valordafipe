using FipeDados.Core.Enums;
using FipeDados.Core.Services;
using FipeDados.Web.Seo;

namespace FipeDados.Web.Consulta;

public static class ConsultaEndpoints
{
    public static void MapConsultaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/consulta/modelos", async (int tipo, int? marca, string? termo, ICatalogService catalog, CancellationToken ct) =>
        {
            if (tipo is not (1 or 2) || marca is <= 0 || termo?.Length > 120)
                return Results.BadRequest(new { erro = "Parâmetros de pesquisa inválidos." });
            return Results.Ok(await catalog.PesquisarModelosAsync((TipoVeiculo)tipo, termo ?? "", marca, ct));
        });
        app.MapGet("/api/consulta/modelos/{modelo:int}/anos", async (int modelo, ICatalogService catalog, PublicCatalog publico, CancellationToken ct) =>
        {
            if (modelo <= 0) return Results.BadRequest(new { erro = "Modelo inválido." });
            // Mantém a descoberta lazy de anos ausentes, sem bloquear o restante do formulário.
            await catalog.ObterAnosAsync(modelo, ct);
            return Results.Ok(await publico.ConsultationYears(modelo, ct));
        });
    }
}
