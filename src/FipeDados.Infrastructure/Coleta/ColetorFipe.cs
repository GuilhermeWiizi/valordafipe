using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Infrastructure.Coleta;

/// <summary>
/// Percorre o catálogo da Fipe (tipo → marca → modelo → ano) reaproveitando os serviços do
/// site: o que já está no banco não vai de novo à Fipe, então a coleta é retomável.
/// Em falha (429/bloqueio) pausa e tenta de novo; falhas seguidas demais abortam a execução.
/// </summary>
public class ColetorFipe
{
    private static readonly TimeSpan IntervaloProgresso = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PausaLimiteMaxima = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<FipeDbContext> _dbFactory;
    private readonly IFipeApiClient _fipe;
    private readonly ICatalogService _catalogo;
    private readonly IPrecoService _precos;
    private readonly ScraperOptions _opt;
    private readonly FipeApiOptions _fipeOpt;
    private readonly ILogger<ColetorFipe> _logger;

    private readonly Stopwatch _relogio = new();
    private TimeSpan _ultimoProgresso;
    private int _falhasSeguidas;
    private int _limitesSeguidos;
    private int _total429;
    private int _itensPulados;
    private int _veiculos;
    private int _precosConsultados;
    private int? _codigoTabela;
    private CatalogoReferenciaSync CatalogoReferencia => new(_dbFactory, _fipe);
    private Func<ProgressoColeta, Task>? _progresso;
    private Func<Task>? _verificarLock;

    public async Task<ProgressoColeta> ExecutarReferenciaAsync(int codigoTabela, Func<ProgressoColeta, Task> progresso, Func<Task> verificarLock, CancellationToken ct)
    {
        _codigoTabela = codigoTabela;
        _progresso = progresso;
        _verificarLock = verificarLock;
        using var semTeto = FipeRequestGate.SemTeto();
        _relogio.Start();
        foreach (var tipo in new[] { TipoVeiculo.Carro, TipoVeiculo.Moto })
        {
            IReadOnlyList<ItemSelecao> marcas = [];
            if (!await TentarAsync($"marcas de {tipo}", async () =>
            {
                marcas = await CatalogoReferencia.MarcasAsync(codigoTabela, tipo, ct);
                return true;
            }, ct)) continue;
            for (var i = 0; i < marcas.Count; i++)
                await ColetarMarcaAsync(tipo, marcas[i], i + 1, marcas.Count, ct);
        }
        var resumo = new ProgressoColeta(_veiculos, _precosConsultados, _itensPulados);
        await progresso(resumo);
        return resumo;
    }

    public ColetorFipe(IDbContextFactory<FipeDbContext> dbFactory, IFipeApiClient fipe, ICatalogService catalogo,
        IPrecoService precos, IOptions<ScraperOptions> opt, IOptions<FipeApiOptions> fipeOpt, ILogger<ColetorFipe> logger)
    {
        _dbFactory = dbFactory;
        _fipe = fipe;
        _catalogo = catalogo;
        _precos = precos;
        _opt = opt.Value;
        _fipeOpt = fipeOpt.Value;
        _logger = logger;
    }

    public async Task ExecutarAsync(CancellationToken ct)
    {
        var tipos = TiposSelecionados();
        using var semTeto = FipeRequestGate.SemTeto();
        _relogio.Start();
        _logger.LogInformation("Coleta iniciada: tipos={Tipos}, marca={Marca}, meses de preço={Meses}, {Rps} req/s.",
            string.Join(",", tipos), Vazio(_opt.Marca) ? "todas" : _opt.Marca, _opt.MesesPreco, _fipeOpt.RequestsPerSecond);

        var mesesTabelas = Math.Max(_fipeOpt.HistoryMonths, _opt.MesesPreco);
        var sincronizou = await TentarAsync("tabelas de referência", async () =>
        {
            var sync = await TabelaReferenciaSync.SincronizarAsync(_fipe, _dbFactory, mesesTabelas, ct);
            if (sync is null) return false;
            _logger.LogInformation("Tabelas de referência: {Novas} novas (total {Total}).", sync.Value.Novas, sync.Value.Total);
            return true;
        }, ct);
        if (!sincronizou)
            throw new ColetaAbortadaException("Não foi possível obter as tabelas de referência da Fipe.");

        foreach (var tipo in tipos)
        {
            IReadOnlyList<ItemSelecao> marcas = [];
            var ok = await TentarAsync($"marcas de {tipo}", async () =>
            {
                marcas = await _catalogo.ObterMarcasAsync(tipo, ct);
                return marcas.Count > 0;
            }, ct);
            if (!ok) continue;

            var selecionadas = Vazio(_opt.Marca)
                ? marcas
                : marcas.Where(m => m.Nome.Contains(_opt.Marca!, StringComparison.OrdinalIgnoreCase)).ToList();
            if (selecionadas.Count == 0)
                _logger.LogWarning("Nenhuma marca de {Tipo} corresponde ao filtro \"{Marca}\".", tipo, _opt.Marca);

            for (var i = 0; i < selecionadas.Count; i++)
                await ColetarMarcaAsync(tipo, selecionadas[i], i + 1, selecionadas.Count, ct);
        }

        _logger.LogInformation(
            "Coleta concluída em {Tempo:hh\\:mm\\:ss}: {Veiculos} veículos percorridos, {Precos} preços consultados na Fipe, {Pulados} itens pulados por falha, {Limites} bloqueios 429.",
            _relogio.Elapsed, _veiculos, _precosConsultados, _itensPulados, _total429);
    }

    private async Task ColetarMarcaAsync(TipoVeiculo tipo, ItemSelecao marca, int posicao, int total, CancellationToken ct)
    {
        var veiculosAntes = _veiculos;
        var precosAntes = _precosConsultados;

        IReadOnlyList<ItemSelecao> modelos = [];
        var ok = await TentarAsync($"modelos de {marca.Nome}", async () =>
        {
            modelos = _codigoTabela is { } tabela
                ? await CatalogoReferencia.ModelosAsync(tabela, marca.Id, ct)
                : await _catalogo.ObterModelosAsync(marca.Id, ct);
            return true;
        }, ct);
        if (!ok) return;
        if (modelos.Count == 0)
            // O CatalogService engole erro de gravação (ex.: chave única), então lista vazia não prova Fipe vazia.
            _logger.LogWarning("Nenhum modelo gravado para {Marca} (Fipe vazia ou erro ao gravar, veja acima).", marca.Nome);

        for (var m = 0; m < modelos.Count; m++)
        {
            var modelo = modelos[m];
            IReadOnlyList<ItemSelecao> anos = [];
            ok = await TentarAsync($"anos de {marca.Nome} {modelo.Nome}", async () =>
            {
                anos = _codigoTabela is { } tabela
                    ? await CatalogoReferencia.AnosAsync(tabela, modelo.Id, ct)
                    : await _catalogo.ObterAnosAsync(modelo.Id, ct);
                return true;
            }, ct);
            if (!ok) continue;
            _veiculos += anos.Count;

            if (_codigoTabela is not null || _opt.MesesPreco > 0)
            {
                foreach (var ano in anos)
                {
                    await TentarAsync($"preço de {marca.Nome} {modelo.Nome} {ano.Nome}", async () =>
                    {
                        var r = _codigoTabela is { } tabela
                            ? await _precos.ColetarPrecoReferenciaAsync(ano.Id, tabela, ct)
                            : await _precos.ColetarPrecosAsync(ano.Id, _opt.MesesPreco, ct);
                        _precosConsultados += r.Consultados;
                        if (r.Falha is not null) ExceptionDispatchInfo.Throw(r.Falha); // 429 x demais falhas
                        return true;
                    }, ct);
                }
            }

            if (_progresso is not null)
                await _progresso(new(_veiculos, _precosConsultados, _itensPulados));

            if (_relogio.Elapsed - _ultimoProgresso >= IntervaloProgresso)
            {
                _ultimoProgresso = _relogio.Elapsed;
                _logger.LogInformation("  ... {Marca}: modelo {Atual}/{Total} | total {Veiculos} veículos, {Precos} preços | {Tempo:hh\\:mm\\:ss}",
                    marca.Nome, m + 1, modelos.Count, _veiculos, _precosConsultados, _relogio.Elapsed);
            }
        }

        _logger.LogInformation("[{Tipo} {Posicao}/{Total}] {Marca}: {Modelos} modelos, {Veiculos} veículos, {Precos} preços novos | {Tempo:hh\\:mm\\:ss}",
            tipo, posicao, total, marca.Nome, modelos.Count, _veiculos - veiculosAntes, _precosConsultados - precosAntes, _relogio.Elapsed);
    }

    /// <summary>
    /// Executa uma etapa. Limite da Fipe (429) não é culpa do item: espera o bloqueio passar e
    /// repete sem gastar tentativa. Demais falhas pausam (cada vez mais) e tentam de novo;
    /// retorna false quando esgota as tentativas e o item fica para a próxima execução.
    /// </summary>
    private async Task<bool> TentarAsync(string etapa, Func<Task<bool>> acao, CancellationToken ct)
    {
        var tentativa = 0;
        while (true)
        {
            // Fora do retry: perder a sessão que sustenta o lock deve abortar esta execução.
            if (_verificarLock is not null) await _verificarLock();
            try
            {
                if (await acao())
                {
                    _falhasSeguidas = 0;
                    _limitesSeguidos = 0;
                    return true;
                }
                tentativa++;
                _logger.LogWarning("Falha em {Etapa} (tentativa {Tentativa}).", etapa, tentativa);
            }
            catch (FipeLimiteException ex) when (!ct.IsCancellationRequested)
            {
                await AguardarLimiteAsync(ex, ct);
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && FalhaDeConexaoComBanco(ex))
            {
                // Banco fora do ar também não é culpa do item nem da Fipe: espera ele voltar.
                await AguardarBancoAsync(etapa, ex, ct);
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                tentativa++;
                _logger.LogWarning("Falha em {Etapa} (tentativa {Tentativa}): {Erro}", etapa, tentativa, ex.Message);
            }

            if (++_falhasSeguidas >= _opt.MaxFalhasConsecutivas)
                throw new ColetaAbortadaException(
                    $"{_falhasSeguidas} falhas seguidas (veja os erros acima). Rode de novo mais tarde; a coleta continua de onde parou.");

            if (tentativa >= _opt.TentativasPorItem)
            {
                _itensPulados++;
                _logger.LogWarning("Pulando {Etapa}; fica para a próxima execução.", etapa);
                return false;
            }

            var pausa = TimeSpan.FromSeconds(_opt.PausaAposFalhaSegundos * tentativa);
            _logger.LogInformation("Pausando {Pausa} antes de tentar de novo.", pausa);
            await Task.Delay(pausa, ct);
        }
    }

    /// <summary>
    /// Espera o bloqueio da Fipe passar: Retry-After quando informado, senão backoff exponencial
    /// a partir de <see cref="ScraperOptions.PausaLimiteSegundos"/> (até 15 min). Bloqueios
    /// seguidos demais abortam a execução.
    /// </summary>
    private async Task AguardarLimiteAsync(FipeLimiteException ex, CancellationToken ct)
    {
        _total429++;
        if (++_limitesSeguidos > _opt.MaxLimitesSeguidos)
            throw new ColetaAbortadaException(
                $"A Fipe recusou {_limitesSeguidos - 1} vezes seguidas (HTTP 429): provável bloqueio. Rode de novo mais tarde; a coleta continua de onde parou.");

        var pausa = ex.RetryAfter ?? TimeSpan.FromSeconds(_opt.PausaLimiteSegundos * Math.Pow(2, _limitesSeguidos - 1));
        if (pausa > PausaLimiteMaxima) pausa = PausaLimiteMaxima;
        _logger.LogWarning("Fipe limitou as requisições (HTTP 429, {Total} nesta execução). Pausando {Pausa}.", _total429, pausa);
        await Task.Delay(pausa, ct);
    }

    /// <summary>
    /// Espera o PostgreSQL voltar a aceitar conexões, conferindo a cada 30 s. Desiste após
    /// <see cref="ScraperOptions.MaxEsperaBancoMinutos"/> sem banco.
    /// </summary>
    private async Task AguardarBancoAsync(string etapa, Exception ex, CancellationToken ct)
    {
        _logger.LogWarning("PostgreSQL indisponível em {Etapa}: {Erro}. Aguardando o banco voltar...", etapa, MensagemRaiz(ex));
        var limite = TimeSpan.FromMinutes(_opt.MaxEsperaBancoMinutos);
        var espera = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                if (await db.Database.CanConnectAsync(ct))
                {
                    _logger.LogInformation("PostgreSQL voltou após {Espera:hh\\:mm\\:ss}; retomando.", espera.Elapsed);
                    return;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // ainda fora do ar
            }

            if (espera.Elapsed >= limite)
                throw new ColetaAbortadaException(
                    $"PostgreSQL indisponível há {_opt.MaxEsperaBancoMinutos} min. Suba o banco (tools/pg-portable.ps1 start) e rode de novo; a coleta continua de onde parou.");
        }
    }

    /// <summary>
    /// Falha de conexão com o banco (servidor fora do ar, porta fechada). <see cref="PostgresException"/>
    /// fica de fora: é erro devolvido por um servidor no ar (ex.: chave duplicada) e esperar não resolve.
    /// </summary>
    private static bool FalhaDeConexaoComBanco(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is NpgsqlException and not PostgresException) return true;
        return false;
    }

    private static string MensagemRaiz(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    private IReadOnlyList<TipoVeiculo> TiposSelecionados()
    {
        if (Vazio(_opt.Tipo)) return [TipoVeiculo.Carro, TipoVeiculo.Moto];
        if (Enum.TryParse<TipoVeiculo>(_opt.Tipo, ignoreCase: true, out var tipo) && Enum.IsDefined(tipo)) return [tipo];
        throw new ArgumentException($"Tipo inválido: \"{_opt.Tipo}\". Use Carro ou Moto.");
    }

    private static bool Vazio(string? s) => string.IsNullOrWhiteSpace(s);
}

public sealed record ProgressoColeta(int Veiculos, int PrecosConsultados, int ItensComFalha);
