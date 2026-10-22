using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FipeDados.Tests;

/// <summary>Executa as consultas reais do PostgreSQL em um schema temporário isolado.</summary>
public class RankingServiceTests
{
    [PostgresFact]
    public async Task Ranking_ComparaMesesConsecutivos_FiltraTipo_OrdenaELimitaCinco()
    {
        var schema = "ranking_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
        // Identificador gerado localmente: apenas prefixo fixo e os dígitos hexadecimais do GUID.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            var service = new Calculador(new Factory(options));
            var vazio = await service.ObterMensalAsync();
            Assert.Null(vazio.Referencia);
            Assert.Empty(vazio.Altas);
            Assert.Empty(vazio.MarcasComMaiorDesvalorizacao);

            var atual = new TabelaReferencia { Codigo = 3, Data = new(2026, 10, 1), Mes = "outubro/2026" };
            var anterior = new TabelaReferencia { Codigo = 2, Data = new(2026, 9, 1), Mes = "setembro/2026" };
            var antigo = new TabelaReferencia { Codigo = 1, Data = new(2026, 8, 1), Mes = "agosto/2026" };
            db.AddRange(atual, anterior, antigo);
            var carro = new Modelo { Nome = "Carro", CodigoFipe = 1, Marca = new Marca { Nome = "Marca", CodigoFipe = 1, Tipo = TipoVeiculo.Carro, Slug = "fiat" } };
            var moto = new Modelo { Nome = "Moto", CodigoFipe = 1, Marca = new Marca { Nome = "Marca", CodigoFipe = 1, Tipo = TipoVeiculo.Moto, Slug = "honda" } };
            var sequencia = 0;
            void Adicionar(Modelo modelo, decimal? valor, decimal? passado, TabelaReferencia? referencia = null)
            {
                var veiculo = new Veiculo { Modelo = modelo, AnoModeloCodigo = (++sequencia).ToString(), AnoLabel = "2025 Gasolina" };
                veiculo.Precos.Add(new PrecoHistorico { TabelaReferencia = atual, Valor = valor });
                veiculo.Precos.Add(new PrecoHistorico { TabelaReferencia = referencia ?? anterior, Valor = passado });
                db.Add(veiculo);
            }
            for (var i = 1; i <= 7; i++)
                Adicionar(new Modelo { Nome = $"Carro {i}", CodigoFipe = i + 1, Slug = $"carro-{i}", Marca = carro.Marca }, 100 + i * 10, 100);
            Adicionar(carro, 80, 100); Adicionar(carro, 50, 100); Adicionar(carro, 100, 100);
            Adicionar(carro, 150, 0); Adicionar(carro, 150, null); Adicionar(carro, null, 100);
            Adicionar(carro, 150, 100, antigo); // Não pode comparar outubro com agosto.
            Adicionar(moto, 200, 100); Adicionar(moto, 25, 100);
            await db.SaveChangesAsync();

            var todos = await service.ObterMensalAsync();
            Assert.Equal(atual.Data, todos.Referencia);
            Assert.Equal(15, todos.PrecosDisponiveis);
            Assert.Equal(12, todos.VeiculosComparados);
            Assert.Equal(5, todos.Altas.Count);
            Assert.Equal(new decimal[] { 100, 70, 60, 50, 40 }, todos.Altas.Select(x => x.VariacaoPct));
            Assert.Equal(new decimal[] { -75, -50 }, todos.Baixas.Select(x => x.VariacaoPct));
            // Apesar das quedas individuais, ambas as marcas tiveram média positiva.
            Assert.Empty(todos.MarcasComMaiorDesvalorizacao);
            var carros = await service.ObterMensalAsync(TipoVeiculo.Carro);
            Assert.Equal(10, carros.VeiculosComparados);
            Assert.All(carros.Altas.Concat(carros.Baixas), x => Assert.Equal(TipoVeiculo.Carro, x.Tipo));
            Assert.Equal(70m, carros.Altas[0].VariacaoPct);
            var motos = await service.ObterMensalAsync(TipoVeiculo.Moto);
            Assert.Equal(2, motos.VeiculosComparados);
            Assert.Single(motos.Altas); Assert.Single(motos.Baixas);

            var marcaMista = new Modelo { Nome = "Misto", CodigoFipe = 1, Marca = new Marca { Nome = "Marca 1", CodigoFipe = 10, Tipo = TipoVeiculo.Carro, Slug = "toyota" } };
            Adicionar(marcaMista, 110, 100); // +10%
            Adicionar(marcaMista, 700, 1000); // -30%; a média percentual simples é -10%, sem ponderação por preço.
            await db.SaveChangesAsync();
            var marcaResultado = Assert.Single((await service.ObterMensalAsync()).MarcasComMaiorDesvalorizacao);
            Assert.Equal(-10m, marcaResultado.VariacaoMediaPct);
            Assert.Equal(2, marcaResultado.VeiculosComparados);

            Adicionar(marcaMista, 100, 100); // Estabilidade também compõe a média.
            Adicionar(marcaMista, 50, 0); // Não há base positiva para calcular variação.
            Adicionar(marcaMista, 50, null);
            Adicionar(marcaMista, 50, 100, antigo); // Agosto não substitui setembro.
            var mesmaMarcaMoto = new Modelo { Nome = "Moto mista", CodigoFipe = 1, Marca = new Marca { Nome = "Marca 1", CodigoFipe = 10, Tipo = TipoVeiculo.Moto, Slug = "yamaha" } };
            Adicionar(mesmaMarcaMoto, 20, 100);
            await db.SaveChangesAsync();
            var separadas = (await service.ObterMensalAsync()).MarcasComMaiorDesvalorizacao;
            Assert.Equal(2, separadas.Count);
            Assert.Equal(TipoVeiculo.Moto, separadas[0].Tipo);
            Assert.Equal(-80m, separadas[0].VariacaoMediaPct);
            Assert.Equal(1, separadas[0].VeiculosComparados);
            Assert.Equal(TipoVeiculo.Carro, separadas[1].Tipo);
            Assert.Equal(3, separadas[1].VeiculosComparados);
            Assert.Equal(-20m / 3, separadas[1].VariacaoMediaPct, 10);

            string[] slugs = ["ford", "hyundai", "jeep", "renault", "nissan", "byd"];
            for (var i = 2; i <= 7; i++)
            {
                var modelo = new Modelo { Nome = $"Modelo {i}", CodigoFipe = 1, Marca = new Marca { Nome = $"Marca {i}", CodigoFipe = 10 + i, Tipo = TipoVeiculo.Carro, Slug = slugs[i - 2] } };
                Adicionar(modelo, 100 - i * 10, 100);
            }
            await db.SaveChangesAsync();
            var topMarcas = (await service.ObterMensalAsync()).MarcasComMaiorDesvalorizacao;
            Assert.Equal(5, topMarcas.Count);
            Assert.Equal(new decimal[] { -80, -70, -60, -50, -40 }, topMarcas.Select(m => m.VariacaoMediaPct));
            var topCarros = (await service.ObterMensalAsync(TipoVeiculo.Carro)).MarcasComMaiorDesvalorizacao;
            Assert.All(topCarros, m => Assert.Equal(TipoVeiculo.Carro, m.Tipo));
            Assert.Equal(new decimal[] { -70, -60, -50, -40, -30 }, topCarros.Select(m => m.VariacaoMediaPct));
            Assert.Single((await service.ObterMensalAsync(TipoVeiculo.Moto)).MarcasComMaiorDesvalorizacao);

            // Marca fora da lista não entra no ranking de marcas, mas o veículo segue nas baixas.
            var obscura = new Modelo { Nome = "Obscuro", CodigoFipe = 1, Marca = new Marca { Nome = "Seres", CodigoFipe = 30, Tipo = TipoVeiculo.Carro, Slug = "seres" } };
            Adicionar(obscura, 1, 100); // -99%
            // A lista é por tipo: royal-enfield só é relevante como moto.
            var reCarro = new Modelo { Nome = "RE carro", CodigoFipe = 1, Marca = new Marca { Nome = "Royal Enfield", CodigoFipe = 31, Tipo = TipoVeiculo.Carro, Slug = "royal-enfield" } };
            var reMoto = new Modelo { Nome = "RE moto", CodigoFipe = 1, Marca = new Marca { Nome = "Royal Enfield", CodigoFipe = 31, Tipo = TipoVeiculo.Moto, Slug = "royal-enfield" } };
            Adicionar(reCarro, 5, 100); // -95%
            Adicionar(reMoto, 10, 100); // -90%
            await db.SaveChangesAsync();
            var filtrado = await service.ObterMensalAsync();
            Assert.Equal("Seres", filtrado.Baixas[0].Marca);
            Assert.DoesNotContain(filtrado.MarcasComMaiorDesvalorizacao, m => m.Marca == "Seres");
            Assert.Equal(new decimal[] { -90, -80, -70, -60, -50 }, filtrado.MarcasComMaiorDesvalorizacao.Select(m => m.VariacaoMediaPct));
            Assert.Equal(TipoVeiculo.Moto, filtrado.MarcasComMaiorDesvalorizacao[0].Tipo);
            Assert.DoesNotContain((await service.ObterMensalAsync(TipoVeiculo.Carro)).MarcasComMaiorDesvalorizacao, m => m.Marca == "Royal Enfield");

            db.Add(new TabelaReferencia { Codigo = 4, Data = new(2026, 11, 1), Mes = "novembro/2026" });
            await db.SaveChangesAsync();
            var semPrecos = await service.ObterMensalAsync();
            Assert.Equal(new DateOnly(2026, 11, 1), semPrecos.Referencia);
            Assert.Equal(0, semPrecos.VeiculosComparados);
            Assert.Empty(semPrecos.Altas); Assert.Empty(semPrecos.Baixas);
            Assert.Empty(semPrecos.MarcasComMaiorDesvalorizacao);
        }
        finally
        {
            // Apenas o schema de teste, cujo nome é gerado nesta execução.
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    [PostgresFact]
    public async Task Ranking_AgrupaDescricaoAntesDoLimite_PreservaMaiorVariacaoEAno()
    {
        var schema = "ranking_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            var atual = new TabelaReferencia { Codigo = 2, Data = new(2026, 10, 1), Mes = "outubro/2026" };
            var anterior = new TabelaReferencia { Codigo = 1, Data = new(2026, 9, 1), Mes = "setembro/2026" };
            var yamaha = new Marca { Nome = "YAMAHA", CodigoFipe = 1, Tipo = TipoVeiculo.Moto, Slug = "yamaha" };
            var rd = new Modelo { Nome = "RD 135", CodigoFipe = 1, Marca = yamaha, Slug = "rd-135" };
            // Até códigos de modelo distintos com a mesma descrição pertencem ao mesmo grupo.
            var duplicado = new Modelo { Nome = "RD 135", CodigoFipe = 2, Marca = yamaha, Slug = "rd-135-2" };
            var sequencia = 0;
            Veiculo Adicionar(Modelo modelo, decimal variacao)
            {
                var ano = 1990 + sequencia++;
                var veiculo = new Veiculo { Modelo = modelo, AnoModeloCodigo = $"{ano}-1", AnoLabel = $"{ano} Gasolina" };
                veiculo.Precos.Add(new PrecoHistorico { TabelaReferencia = atual, Valor = 100 + variacao });
                veiculo.Precos.Add(new PrecoHistorico { TabelaReferencia = anterior, Valor = 100 });
                db.Add(veiculo);
                return veiculo;
            }
            var maiorAlta = Adicionar(rd, 50);
            var maiorQueda = Adicionar(rd, -50);
            foreach (var variacao in new decimal[] { 49, 48, 47, 46, 50 })
            {
                Adicionar(duplicado, variacao);
                Adicionar(duplicado, -variacao);
            }
            for (var i = 0; i < 6; i++)
            {
                var modelo = new Modelo { Nome = $"Modelo {i}", CodigoFipe = 3 + i, Marca = yamaha, Slug = $"modelo-{i}" };
                Adicionar(modelo, 40 - i * 5);
                Adicionar(modelo, -40 + i * 5);
            }
            var outraMarca = new Modelo { Nome = "RD 135", CodigoFipe = 1, Slug = "rd-135", Marca = new Marca { Nome = "Outra", CodigoFipe = 2, Tipo = TipoVeiculo.Moto, Slug = "outra" } };
            var outroTipo = new Modelo { Nome = "RD 135", CodigoFipe = 1, Slug = "rd-135", Marca = new Marca { Nome = "YAMAHA", CodigoFipe = 1, Tipo = TipoVeiculo.Carro, Slug = "yamaha" } };
            Adicionar(outraMarca, 60); Adicionar(outraMarca, -60);
            Adicionar(outroTipo, 55); Adicionar(outroTipo, -55);
            await db.SaveChangesAsync();

            var service = new Calculador(new Factory(options));
            var todos = await service.ObterMensalAsync();
            Assert.Equal(28, todos.VeiculosComparados);
            Assert.Equal(new decimal[] { 60, 55, 50, 40, 35 }, todos.Altas.Select(x => x.VariacaoPct));
            Assert.Equal(new decimal[] { -60, -55, -50, -40, -35 }, todos.Baixas.Select(x => x.VariacaoPct));
            var alta = todos.Altas[2];
            Assert.Equal(maiorAlta.Id, alta.VeiculoId); // Empates usam o menor ID.
            Assert.Equal(maiorAlta.AnoLabel, alta.AnoLabel);
            Assert.Equal(maiorAlta.AnoModeloCodigo, alta.AnoModeloCodigo);
            Assert.Equal(150m, alta.ValorAtual);
            Assert.Equal("yamaha", alta.MarcaSlug);
            Assert.Equal("rd-135", alta.ModeloSlug);
            Assert.Equal(maiorQueda.Id, todos.Baixas[2].VeiculoId);
            Assert.Equal(maiorQueda.AnoLabel, todos.Baixas[2].AnoLabel);
            Assert.Equal(50m, todos.Baixas[2].ValorAtual);

            var motos = await service.ObterMensalAsync(TipoVeiculo.Moto);
            Assert.Equal(new decimal[] { 60, 50, 40, 35, 30 }, motos.Altas.Select(x => x.VariacaoPct));
            Assert.Equal(new decimal[] { -60, -50, -40, -35, -30 }, motos.Baixas.Select(x => x.VariacaoPct));
            var carros = await service.ObterMensalAsync(TipoVeiculo.Carro);
            Assert.Equal(55m, Assert.Single(carros.Altas).VariacaoPct);
            Assert.Equal(-55m, Assert.Single(carros.Baixas).VariacaoPct);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    [PostgresFact]
    public async Task Ranking_PublicadoPersiste_AtualizacaoInvalidaMemoria_EPublicaTresFiltros()
    {
        var schema = "ranking_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new RankingService(new Factory(options), cache);
            var atualizador = new RankingAtualizador(new Factory(options), TimeProvider.System, [], NullLogger<RankingAtualizador>.Instance);
            var atual = new TabelaReferencia { Codigo = 2, Data = new(2026, 10, 1), Mes = "outubro/2026" };
            var anterior = new TabelaReferencia { Codigo = 1, Data = new(2026, 9, 1), Mes = "setembro/2026" };
            var modelo = new Modelo { Nome = "Carro", CodigoFipe = 1, Marca = new Marca { Nome = "Marca", CodigoFipe = 1, Tipo = TipoVeiculo.Carro } };
            var sequencia = 0;
            void Adicionar(decimal valor)
            {
                var veiculo = new Veiculo { Modelo = modelo, AnoModeloCodigo = (++sequencia).ToString(), AnoLabel = "2025 Gasolina" };
                veiculo.Precos.Add(new PrecoHistorico { TabelaReferencia = atual, Valor = valor });
                veiculo.Precos.Add(new PrecoHistorico { TabelaReferencia = anterior, Valor = 100 });
                db.Add(veiculo);
            }
            Adicionar(110);
            await db.SaveChangesAsync();
            Assert.Null((await service.ObterMensalAsync()).Referencia); // Uma visita não faz agregações.
            await atualizador.AtualizarAsync();
            var primeiro = await service.ObterMensalAsync();
            Assert.Equal(1, primeiro.VeiculosComparados);
            Assert.Equal(3, await db.RankingsMensais.CountAsync());
            Assert.Single(await db.RankingsMensais.Select(r => r.Versao).Distinct().ToListAsync());

            Adicionar(90);
            await db.SaveChangesAsync();
            Assert.Same(primeiro, await service.ObterMensalAsync()); // Dentro da validade: sem novas agregações.
            Assert.Equal(1, (await service.ObterMensalAsync(TipoVeiculo.Carro)).VeiculosComparados);
            Assert.Equal(1, (await new RankingService(new Factory(options)).ObterMensalAsync()).VeiculosComparados); // Reinício reaproveita o banco.
            await atualizador.AtualizarAsync(); // Dentro do intervalo não recalcula.
            Assert.Same(primeiro, await service.ObterMensalAsync());
            await atualizador.AtualizarAsync(forcar: true);
            var atualizado = await service.ObterMensalAsync();
            Assert.NotSame(primeiro, atualizado);
            Assert.Equal(2, atualizado.VeiculosComparados);
            Assert.Single(atualizado.Baixas);

            db.Add(new TabelaReferencia { Codigo = 3, Data = new(2026, 11, 1), Mes = "novembro/2026" });
            await db.SaveChangesAsync();
            Assert.Equal(atual.Data, (await service.ObterMensalAsync()).Referencia); // Última publicação até o job terminar.
            await atualizador.AtualizarAsync();
            var novo = await service.ObterMensalAsync();
            Assert.Equal(new DateOnly(2026, 11, 1), novo.Referencia);
            Assert.Equal(6, await db.RankingsMensais.CountAsync());
            // Nem mesmo um processo sem cache precisa acessar a tabela de preços para ler.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Precos\" RENAME TO \"Precos_indisponiveis\"");
            Assert.Equal(novo.Referencia, (await new RankingService(new Factory(options)).ObterMensalAsync()).Referencia);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

#pragma warning restore EF1002
    private class Calculador(Factory factory)
    {
        public async Task<RankingMensal> ObterMensalAsync(TipoVeiculo? tipo = null)
        {
            await using var db = factory.CreateDbContext();
            var atual = await db.TabelasReferencia.OrderByDescending(t => t.Data).FirstOrDefaultAsync();
            return atual is null ? new(null, 0, 0, [], []) : await RankingCalculator.CalcularAsync(db, atual, tipo, default);
        }
    }

    private class Factory(DbContextOptions<FipeDbContext> options) : IDbContextFactory<FipeDbContext>
    {
        public FipeDbContext CreateDbContext() => new(options);
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")))
            Skip = "Defina FIPEDADOS_TEST_CONNECTION para executar a integração PostgreSQL em schema isolado.";
    }
}
