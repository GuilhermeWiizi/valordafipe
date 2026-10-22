using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FipeDados.Infrastructure.Services;

/// <summary>Relaciona veículos apenas por preços da mesma referência; não dispara coleta em lote.</summary>
public class VeiculosRelacionadosService(IDbContextFactory<FipeDbContext> factory) : IVeiculosRelacionadosService
{
    public async Task<VeiculosRelacionados> ObterAsync(int veiculoId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var veiculo = await db.Veiculos.AsNoTracking().Include(v => v.Modelo).ThenInclude(m => m.Marca)
            .FirstOrDefaultAsync(v => v.Id == veiculoId, ct);
        if (veiculo is null) return new(null, null, [], []);
        var referencias = db.TabelasReferencia.OrderByDescending(t => t.Data).Take(24).Select(t => t.Codigo);
        var precoBase = await db.Precos.AsNoTracking().Where(p => p.VeiculoId == veiculoId && p.Valor > 0 && referencias.Contains(p.TabelaReferenciaCodigo))
            .OrderByDescending(p => p.TabelaReferencia.Data)
            .Select(p => new { p.TabelaReferenciaCodigo, p.TabelaReferencia.Data, Valor = p.Valor!.Value }).FirstOrDefaultAsync(ct);
        var codigo = precoBase?.TabelaReferenciaCodigo ?? -1;
        var valor = precoBase?.Valor;
        var precosDoMes = db.Precos.AsNoTracking().Where(p => p.TabelaReferenciaCodigo == codigo && p.Valor > 0);
        var anos = await (from v in db.Veiculos.AsNoTracking()
                          where v.ModeloId == veiculo.ModeloId
                          join p in precosDoMes on v.Id equals p.VeiculoId into precos
                          from p in precos.DefaultIfEmpty()
                          orderby v.ZeroKm descending, v.Ano descending, v.Combustivel, v.Id
                          select new VeiculoRelacionado(v.Id, veiculo.Modelo.Marca.Nome, veiculo.Modelo.Nome,
                              v.AnoLabel, p == null ? null : p.Valor, p == null ? null : p.Valor - valor,
                              veiculo.Modelo.Marca.Tipo, veiculo.Modelo.Marca.Slug, veiculo.Modelo.Slug, v.AnoModeloCodigo))
            .Take(30).ToListAsync(ct);
        var semelhantes = new List<VeiculoRelacionado>();
        if (valor is { } basePositiva)
        {
            var minimo = basePositiva * .9m;
            var maximo = basePositiva * 1.1m;
            var candidatos = precosDoMes.Where(p => p.Veiculo.ModeloId != veiculo.ModeloId
                && p.Veiculo.Modelo.Marca.Tipo == veiculo.Modelo.Marca.Tipo && p.Valor >= minimo && p.Valor <= maximo);
            if (veiculo.Modelo.Marca.Tipo == TipoVeiculo.Carro)
            {
                var marcas = MarcasRelevantes.CarrosPopulares;
                candidatos = candidatos.Where(p => marcas.Contains(p.Veiculo.Modelo.Marca.Slug));
            }
            semelhantes = await candidatos
                .OrderBy(p => Math.Abs(p.Valor!.Value - basePositiva)).ThenBy(p => p.VeiculoId).Take(6)
                .Select(p => new VeiculoRelacionado(p.VeiculoId, p.Veiculo.Modelo.Marca.Nome, p.Veiculo.Modelo.Nome,
                    p.Veiculo.AnoLabel, p.Valor, p.Valor - basePositiva, p.Veiculo.Modelo.Marca.Tipo,
                    p.Veiculo.Modelo.Marca.Slug, p.Veiculo.Modelo.Slug, p.Veiculo.AnoModeloCodigo)).ToListAsync(ct);
        }
        return new(precoBase?.Data, valor, anos, semelhantes);
    }
}
