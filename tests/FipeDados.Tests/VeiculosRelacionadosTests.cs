using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FipeDados.Tests;

public class VeiculosRelacionadosTests
{
    [PostgresFact]
    public async Task RelacionaAnosEPrecos_UsaMesmaReferenciaTipoEFaixa_ExcluiProprioModelo()
    {
        var schema = "related_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
        // Identificador com prefixo fixo e GUID hexadecimal gerado localmente.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            db.AddRange(new TabelaReferencia { Codigo = 1, Data = new(2026, 9, 1), Mes = "setembro/2026" }, new TabelaReferencia { Codigo = 2, Data = new(2026, 10, 1), Mes = "outubro/2026" });
            var marca = new Marca { Nome = "Marca", Tipo = TipoVeiculo.Carro, CodigoFipe = 1 };
            var modelo = new Modelo { Nome = "Modelo base", Marca = marca, CodigoFipe = 1 };
            Veiculo V(Modelo m, int ano, decimal? preco, int tabela = 2)
            {
                var v = new Veiculo { Modelo = m, Ano = ano, AnoLabel = $"{ano} Gasolina", AnoModeloCodigo = $"{ano}-1" };
                if (preco is not null) v.Precos.Add(new PrecoHistorico { TabelaReferenciaCodigo = tabela, Valor = preco });
                db.Add(v); return v;
            }
            var atual = V(modelo, 2021, 100);
            var anoNovo = V(modelo, 2022, 120);
            var anoAntigo = V(modelo, 2020, 90, 1); // Não mostra setembro junto com preços de outubro.
            var proximo = V(new Modelo { Nome = "Próximo", Marca = marca, CodigoFipe = 2 }, 2021, 103);
            var abaixo = V(new Modelo { Nome = "Limite inferior", Marca = marca, CodigoFipe = 3 }, 2021, 90);
            var acima = V(new Modelo { Nome = "Limite superior", Marca = marca, CodigoFipe = 4 }, 2021, 110);
            V(new Modelo { Nome = "Caro demais", Marca = marca, CodigoFipe = 5 }, 2021, 111);
            V(new Modelo { Nome = "Preço antigo", Marca = marca, CodigoFipe = 6 }, 2021, 100, 1);
            V(new Modelo { Nome = "Preço zerado", Marca = marca, CodigoFipe = 7 }, 2021, 0);
            V(new Modelo { Nome = "Moto", CodigoFipe = 1, Marca = new Marca { Nome = "Outra", Tipo = TipoVeiculo.Moto, CodigoFipe = 1 } }, 2021, 100);
            var semPreco = V(new Modelo { Nome = "Sem preço", Marca = marca, CodigoFipe = 8 }, 2021, null);
            await db.SaveChangesAsync();
            var service = new VeiculosRelacionadosService(new Factory(options));
            var r = await service.ObterAsync(atual.Id);
            Assert.Equal(new DateOnly(2026, 10, 1), r.Referencia);
            Assert.Equal(100, r.PrecoBase);
            Assert.Equal(3, r.OutrosAnos.Count);
            Assert.Equal(120, r.OutrosAnos.Single(v => v.Id == anoNovo.Id).Preco);
            Assert.Equal(20, r.OutrosAnos.Single(v => v.Id == anoNovo.Id).DiferencaValor);
            Assert.Null(r.OutrosAnos.Single(v => v.Id == anoAntigo.Id).Preco);
            Assert.Equal(new[] { proximo.Id, abaixo.Id, acima.Id }, r.PrecosSemelhantes.Select(v => v.Id));
            Assert.Equal(3, r.PrecosSemelhantes[0].DiferencaValor);
            var vazio = await service.ObterAsync(semPreco.Id);
            Assert.Null(vazio.Referencia); Assert.Empty(vazio.PrecosSemelhantes);
            Assert.Single(vazio.OutrosAnos);
            Assert.Empty((await service.ObterAsync(int.MaxValue)).OutrosAnos);
        }
        finally { await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE"); }
#pragma warning restore EF1002
    }
    private class Factory(DbContextOptions<FipeDbContext> options) : IDbContextFactory<FipeDbContext>
    {
        public FipeDbContext CreateDbContext() => new(options);
    }
}
