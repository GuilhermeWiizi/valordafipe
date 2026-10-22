using System.Globalization;
using FipeDados.Core.Entities;
using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Infrastructure.Services;

/// <summary>
/// Monta o detalhe do veículo, garantindo sob demanda os preços dos últimos N meses.
/// A primeira consulta usa o modo "tradicional" para descobrir o código Fipe; as
/// demais usam o modo "codigo" (mais robusto entre meses). Lacunas (mês sem preço)
/// são gravadas com valor nulo para não reconsultar.
/// </summary>
public class PrecoService : IPrecoService
{
    private readonly IDbContextFactory<FipeDbContext> _dbFactory;
    private readonly IFipeApiClient _fipe;
    private readonly FipeApiOptions _options;
    private readonly ILogger<PrecoService> _logger;
    private readonly ColetaPorVeiculoLock _coletas;

    public PrecoService(IDbContextFactory<FipeDbContext> dbFactory, IFipeApiClient fipe,
        IOptions<FipeApiOptions> options, ILogger<PrecoService> logger, ColetaPorVeiculoLock? coletas = null)
    {
        _dbFactory = dbFactory;
        _fipe = fipe;
        _options = options.Value;
        _logger = logger;
        _coletas = coletas ?? new ColetaPorVeiculoLock();
    }

    public async Task<DetalheVeiculo?> ObterDetalheAsync(int veiculoId, CancellationToken ct = default)
        => await MontarDetalheAsync(veiculoId, coletar: true, ct);

    public async Task<DetalheVeiculo?> ObterDetalheEmCacheAsync(int veiculoId, CancellationToken ct = default)
        => await MontarDetalheAsync(veiculoId, coletar: false, ct);

    private async Task<DetalheVeiculo?> MontarDetalheAsync(int veiculoId, bool coletar, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var veiculo = await CarregarVeiculoAsync(db, veiculoId, ct);
        if (veiculo is null) return null;

        var tabelas = await UltimasTabelasAsync(db, _options.HistoryMonths, ct);
        if (coletar && tabelas.Count > 0)
            await GarantirPrecosAsync(db, veiculo, tabelas, ct);

        var codigos = tabelas.Select(t => t.Codigo).ToHashSet();
        var precos = await db.Precos
            .Where(p => p.VeiculoId == veiculoId && codigos.Contains(p.TabelaReferenciaCodigo))
            .ToListAsync(ct);

        var porCodigo = tabelas.ToDictionary(t => t.Codigo);
        var serie = precos
            .Where(p => porCodigo.ContainsKey(p.TabelaReferenciaCodigo))
            .Select(p =>
            {
                var t = porCodigo[p.TabelaReferenciaCodigo];
                return new PontoPreco(t.Codigo, t.Data, t.Mes, p.Valor);
            })
            .OrderBy(p => p.Data)
            .ToList();

        var resumo = VariacaoCalculator.Calcular(serie);

        return new DetalheVeiculo(
            veiculo.Id,
            veiculo.Modelo.Marca.Tipo,
            veiculo.Modelo.Marca.Nome,
            veiculo.Modelo.Nome,
            veiculo.AnoLabel,
            veiculo.Combustivel.ToString(),
            veiculo.CodigoFipe,
            serie,
            resumo,
            veiculo.Modelo.MarcaId,
            veiculo.ModeloId,
            veiculo.Modelo.Marca.Slug,
            veiculo.Modelo.Slug,
            veiculo.AnoModeloCodigo);
    }

    public async Task<ResultadoColeta> ColetarPrecosAsync(int veiculoId, int meses, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var veiculo = await CarregarVeiculoAsync(db, veiculoId, ct);
        if (veiculo is null) return new ResultadoColeta(0, null);

        var tabelas = await UltimasTabelasAsync(db, meses, ct);
        return await GarantirPrecosAsync(db, veiculo, tabelas, ct);
    }

    public async Task<ResultadoColeta> CompletarHistoricoAsync(int veiculoId, int meses, CancellationToken ct = default, bool reconsultarLacunas = true)
    {
        if (meses is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(meses));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var veiculo = await CarregarVeiculoAsync(db, veiculoId, ct);
        if (veiculo is null) return new(0, new InvalidOperationException("Veículo não encontrado."));

        var tabelas = await UltimasTabelasAsync(db, meses, ct);
        // O job de fundo pode ainda não ter preenchido as referências necessárias.
        if (tabelas.Count < meses || Enumerable.Range(0, meses)
            .Any(i => !tabelas.Any(t => t.Data == tabelas[0].Data.AddMonths(-i))))
        {
            try
            {
                await TabelaReferenciaSync.SincronizarAsync(_fipe, _dbFactory, Math.Max(meses, _options.HistoryMonths), ct);
                tabelas = await UltimasTabelasAsync(db, meses, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(0, ex);
            }
        }
        if (tabelas.Count == 0) return new(0, new InvalidOperationException("A Fipe não retornou tabelas de referência."));
        return await GarantirPrecosAsync(db, veiculo, tabelas, ct, reconsultarLacunas, salvarCadaMes: true);
    }

    public async Task<ResultadoColeta> ColetarPrecoReferenciaAsync(int veiculoId, int codigoTabela, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var veiculo = await CarregarVeiculoAsync(db, veiculoId, ct);
        var tabela = await db.TabelasReferencia.FindAsync([codigoTabela], ct);
        if (veiculo is null || tabela is null) return new(0, new InvalidOperationException("Veículo ou referência não encontrado."));
        return await GarantirPrecosAsync(db, veiculo, [tabela], ct, salvarCadaMes: true);
    }

    public async Task<ConsultaNavegador?> ObterConsultaNavegadorAsync(int veiculoId, int meses, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var veiculo = await CarregarVeiculoAsync(db, veiculoId, ct);
        if (veiculo is null) return null;

        var tabelas = await UltimasTabelasAsync(db, meses, ct);
        var codigos = tabelas.Select(t => t.Codigo).ToList();
        // Meses já gravados, inclusive sem cotação: o navegador não os consulta de novo.
        var conhecidas = await db.Precos.AsNoTracking()
            .Where(p => p.VeiculoId == veiculoId && codigos.Contains(p.TabelaReferenciaCodigo))
            .Select(p => p.TabelaReferenciaCodigo)
            .ToListAsync(ct);
        var marca = veiculo.Modelo.Marca;
        return new ConsultaNavegador(
            (int)marca.Tipo,
            marca.CodigoFipe,
            veiculo.Modelo.CodigoFipe,
            veiculo.AnoModeloCodigo,
            veiculo.ZeroKm ? 32000 : (veiculo.Ano ?? 0),
            (int)veiculo.Combustivel,
            string.IsNullOrWhiteSpace(veiculo.CodigoFipe) ? null : veiculo.CodigoFipe.Trim(),
            meses,
            tabelas.Select(t => new ReferenciaNavegador(t.Codigo, t.Data.ToString("yyyy-MM", CultureInfo.InvariantCulture))).ToList(),
            conhecidas);
    }

    private static Task<Veiculo?> CarregarVeiculoAsync(FipeDbContext db, int veiculoId, CancellationToken ct)
        => db.Veiculos
            .Include(v => v.Modelo).ThenInclude(m => m.Marca)
            .FirstOrDefaultAsync(v => v.Id == veiculoId, ct);

    private static Task<List<TabelaReferencia>> UltimasTabelasAsync(FipeDbContext db, int meses, CancellationToken ct)
        => db.TabelasReferencia
            .OrderByDescending(t => t.Data)
            .Take(meses)
            .ToListAsync(ct);

    private async Task<ResultadoColeta> GarantirPrecosAsync(FipeDbContext db, Veiculo veiculo, List<TabelaReferencia> tabelas, CancellationToken ct,
        bool reconsultarLacunas = false, bool salvarCadaMes = false)
    {
        bool Falta(bool existe, decimal? valor) => !existe || (reconsultarLacunas && !(valor > 0));

        // Checagem sem trava: veículo já completo (o caso comum) não espera a vez de ninguém.
        var conhecidos = await db.Precos.AsNoTracking()
            .Where(p => p.VeiculoId == veiculo.Id)
            .Select(p => new { p.TabelaReferenciaCodigo, p.Valor })
            .ToDictionaryAsync(p => p.TabelaReferenciaCodigo, p => p.Valor, ct);
        if (!tabelas.Any(t => Falta(conhecidos.TryGetValue(t.Codigo, out var v), v)))
            return new ResultadoColeta(0, null);

        // Outra coleta deste veículo pode estar em andamento: espera a vez e relê o que ela gravou.
        using var vez = await _coletas.AcquireAsync(veiculo.Id, ct);
        await db.Entry(veiculo).ReloadAsync(ct);

        var existentes = (await db.Precos
            .Where(p => p.VeiculoId == veiculo.Id)
            .ToListAsync(ct)).ToDictionary(p => p.TabelaReferenciaCodigo);

        var faltantes = tabelas
            .Where(t => Falta(existentes.TryGetValue(t.Codigo, out var p), p?.Valor))
            .OrderByDescending(t => t.Data) // mais recente primeiro: descobre o código Fipe cedo
            .ToList();
        if (faltantes.Count == 0) return new ResultadoColeta(0, null);

        var modelo = veiculo.Modelo;
        var marca = modelo.Marca;
        var anoNumerico = veiculo.ZeroKm ? 32000 : (veiculo.Ano ?? 0);
        var codigoComb = (int)veiculo.Combustivel;
        var consultados = 0;
        Exception? falha = null;

        foreach (var tabela in faltantes)
        {
            ct.ThrowIfCancellationRequested();

            var usarCodigo = !string.IsNullOrWhiteSpace(veiculo.CodigoFipe);
            var query = new FipeValorQuery(
                CodigoTabela: tabela.Codigo,
                Tipo: marca.Tipo,
                CodigoMarca: marca.CodigoFipe,
                CodigoModelo: modelo.CodigoFipe,
                Ano: veiculo.AnoModeloCodigo,
                AnoNumerico: anoNumerico,
                CodigoTipoCombustivel: codigoComb,
                TipoConsulta: usarCodigo ? "codigo" : "tradicional",
                ModeloCodigoExterno: usarCodigo ? veiculo.CodigoFipe : null);

            FipeValorResponse? resp;
            try
            {
                resp = await _fipe.ConsultarValorAsync(query, ct);
                // Algumas versões não são encontradas pelo código em todos os meses.
                if (salvarCadaMes && usarCodigo && !(FipeParsing.ParseValor(resp?.Valor) > 0))
                    resp = await _fipe.ConsultarValorAsync(query with { TipoConsulta = "tradicional", ModeloCodigoExterno = null }, ct);
                if (resp is not null && !(FipeParsing.ParseValor(resp.Valor) > 0))
                    throw new HttpRequestException("A Fipe retornou uma cotação inválida; o preço permanece pendente.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provável instabilidade/bloqueio: para de insistir nesta sessão e
                // mantém o que já foi coletado. Os meses restantes ficam para depois.
                _logger.LogWarning("Falha ao consultar preço do veículo {Veiculo} na tabela {Tabela}: {Erro}", veiculo.Id, tabela.Codigo, ex.Message);
                falha = ex;
                break;
            }

            if (string.IsNullOrWhiteSpace(veiculo.CodigoFipe) && !string.IsNullOrWhiteSpace(resp?.CodigoFipe))
                veiculo.CodigoFipe = resp.CodigoFipe!.Trim();

            if (!existentes.TryGetValue(tabela.Codigo, out var preco))
            {
                preco = new PrecoHistorico { VeiculoId = veiculo.Id, TabelaReferenciaCodigo = tabela.Codigo };
                db.Precos.Add(preco);
            }
            preco.Valor = FipeParsing.ParseValor(resp?.Valor);
            preco.ColetadoEm = DateTime.UtcNow;
            consultados++;
            if (salvarCadaMes)
            {
                // A comparação pode ser cancelada entre requisições: mantém cada mês já obtido.
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException ex)
                {
                    if (await ConflitoJaGravadoAsync(db, ex, veiculo.Id, [tabela.Codigo], ct))
                    {
                        db.Entry(preco).State = EntityState.Detached;
                        db.Entry(veiculo).State = EntityState.Unchanged;
                        continue;
                    }
                    _logger.LogWarning(ex, "Conflito ao completar histórico do veículo {Veiculo}.", veiculo.Id);
                    return new(consultados, ex);
                }
            }
        }

        if (consultados == 0) return new ResultadoColeta(0, falha);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            if (await ConflitoJaGravadoAsync(db, ex, veiculo.Id, faltantes.Select(t => t.Codigo).ToArray(), ct))
                return new(consultados, falha);
            _logger.LogWarning(ex, "Conflito ao gravar preços do veículo {Veiculo}.", veiculo.Id);
            falha ??= ex;
        }
        return new ResultadoColeta(consultados, falha);
    }

    private static async Task<bool> ConflitoJaGravadoAsync(FipeDbContext db, DbUpdateException ex, int veiculoId, int[] tabelas, CancellationToken ct)
    {
        if (ex.InnerException is not PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return false;
        // Outra consulta (site/coletor) pode ter gravado o mesmo preço durante a chamada HTTP.
        return await db.Precos.AsNoTracking().CountAsync(p => p.VeiculoId == veiculoId && tabelas.Contains(p.TabelaReferenciaCodigo), ct) == tabelas.Length;
    }
}
