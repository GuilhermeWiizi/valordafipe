using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Web.Seo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FipeDados.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Corolla XEi 2.0 Flex 16V Aut.", "corolla-xei-2-0-flex-16v-aut")]
    [InlineData("GM - Chevrolet", "gm-chevrolet")]
    [InlineData("Citroën", "citroen")]
    [InlineData("MAGRÃO TRICICLOS", "magrao-triciclos")]
    [InlineData("Scénic Alizé/ Expression 1.6 16V Mec.", "scenic-alize-expression-1-6-16v-mec")]
    [InlineData("Ranger Limited+ 3.0 V6 4x4 CD TB Die Aut", "ranger-limited-plus-3-0-v6-4x4-cd-tb-die-aut")]
    [InlineData("Marca & Cia", "marca-e-cia")]
    [InlineData("L'AQUILA", "l-aquila")]
    [InlineData("  --Onix  Plus--  ", "onix-plus")]
    [InlineData("Ŝkoda Ĉ", "skoda-c")]
    [InlineData("!!!", "")]
    public void Gerar_MinusculasSemAcentoComHifens(string nome, string esperado)
        => Assert.Equal(esperado, Slugs.Gerar(nome));

    [Fact]
    public void Gerar_DistingueVersoesComMais()
        => Assert.NotEqual(Slugs.Gerar("Jimny Sierra 4YOU+ ALLGRIP 1.5 16V Aut."), Slugs.Gerar("Jimny Sierra 4YOU ALLGRIP 1.5 16V Aut."));

    [Fact]
    public void Base_SemLetrasNemDigitos_UsaCodigoFipe()
    {
        Assert.Equal("4321", Slugs.Base("!!!", 4321));
        Assert.Equal("uno", Slugs.Base("Uno", 4321));
    }

    [Fact]
    public void Unico_UsaPrimeiroSufixoLivre_ERegistraOEscolhido()
    {
        var ocupados = new HashSet<string> { "onix", "onix-2", "onix-4" };
        Assert.Equal("onix-3", Slugs.Unico("onix", ocupados));
        Assert.Equal("onix-5", Slugs.Unico("onix", ocupados));
        Assert.Equal("uno", Slugs.Unico("uno", ocupados));
        Assert.Contains("onix-3", ocupados);
    }

    [Theory]
    [InlineData("2019-1", "2019-gasolina")]
    [InlineData("1985-2", "1985-alcool")]
    [InlineData("2020-3", "2020-diesel")]
    [InlineData("2021-4", "2021-eletrico")]
    [InlineData("2019-5", "2019-flex")]
    [InlineData("2022-6", "2022-hibrido")]
    [InlineData("2016-7", "2016-gas-natural")]
    [InlineData("32000-5", "zero-km-flex")]
    [InlineData("2019-9", "2019-combustivel-9")]
    [InlineData("excluido0", "excluido0")]
    [InlineData("", "ano")]
    public void AnoCombustivel_DerivaDoCodigoAnoModelo(string codigo, string esperado)
        => Assert.Equal(esperado, Slugs.AnoCombustivel(codigo));

    [Fact]
    public void AnoCombustivel_EInjetivo_LogoUnicoDentroDoModelo()
    {
        // O índice único (ModeloId, AnoModeloCodigo) garante a unicidade da URL se nenhum par colidir.
        var codigos = Enumerable.Range(1950, 81).Append(32000)
            .SelectMany(ano => Enumerable.Range(0, 10).Select(c => $"{ano}-{c}")).ToList();
        Assert.Equal(codigos.Count, codigos.Select(Slugs.AnoCombustivel).Distinct().Count());
    }

    [Theory]
    [InlineData("32000 Flex", "Zero KM Flex")]
    [InlineData("32000", "Zero KM")]
    [InlineData("2019 Gasolina", "2019 Gasolina")]
    public void RotuloAno_TrocaCodigoZeroKm(string rotulo, string esperado)
        => Assert.Equal(esperado, FipeParsing.RotuloAno(rotulo));

    [Fact]
    public void CatalogPaths_MontaHierarquiaPorTipo()
    {
        Assert.Equal("/tabela-fipe/carros/toyota", CatalogPaths.Brand(TipoVeiculo.Carro, "toyota"));
        Assert.Equal("/tabela-fipe/motos/honda/cg-160-fan", CatalogPaths.Model(TipoVeiculo.Moto, "honda", "cg-160-fan"));
        Assert.Equal("/tabela-fipe/carros/toyota/corolla-xei-2-0-flex-16v-aut/2019-flex",
            CatalogPaths.Vehicle(TipoVeiculo.Carro, "toyota", "corolla-xei-2-0-flex-16v-aut", "2019-5"));
        Assert.Equal("/tabela-fipe/carros/toyota?pagina=2", CatalogPaths.Paged("/tabela-fipe/carros/toyota", 2));
        Assert.Equal("/tabela-fipe/carros/toyota", CatalogPaths.Paged("/tabela-fipe/carros/toyota", 1));
    }

    [Theory]
    [InlineData("carros", TipoVeiculo.Carro)]
    [InlineData("MOTOS", TipoVeiculo.Moto)]
    [InlineData("caminhoes", null)]
    [InlineData("carro", null)]
    public void CatalogPaths_ParseType(string segmento, TipoVeiculo? esperado)
        => Assert.Equal(esperado, CatalogPaths.ParseType(segmento));

    [Theory]
    [InlineData("002096-8", "002096-8")]
    [InlineData("0020968", "002096-8")]
    [InlineData("002096 8", "002096-8")]
    [InlineData("2096-8", null)]
    [InlineData("002096-8x", null)]
    [InlineData("abc", null)]
    public void CatalogPaths_NormalizeCode(string valor, string? esperado)
        => Assert.Equal(esperado, CatalogPaths.NormalizeCode(valor));

    [PostgresFact]
    public async Task MigrationAddSlugs_PreencheLinhasExistentes_ComOAlgoritmoDoCodigo()
    {
        var schema = "slug_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
        // Identificador gerado localmente: apenas prefixo fixo e os dígitos hexadecimais do GUID.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260602003730_Initial");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Marcas" ("Id", "Tipo", "CodigoFipe", "Nome")
                VALUES (1, 1, 10, 'GM - Chevrolet'), (2, 2, 10, 'GM - Chevrolet'), (3, 1, 11, 'Citroën')
                """);
            string[] nomes =
            [
                "Ranger Limited 3.0 V6 4x4 CD TB Die. Aut", "Ranger Limited+ 3.0 V6 4x4 CD TB Die Aut",
                "Citroën C4 Picasso", "MAGRÃO", "Ação & Reação", "Scénic Alizé/ Expression 1.6", "Ŝkoda Ĉ", "!!!",
                "Onix 1.0", "Onix 1-0", "ONIX 1.0", "Onix 1-0-2"
            ];
            for (var i = 0; i < nomes.Length; i++)
                await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Modelos" ("Id", "MarcaId", "CodigoFipe", "Nome") VALUES ({0}, 1, {1}, {2})""",
                    i + 1, 100 + i, nomes[i]);

            await migrator.MigrateAsync();

            Assert.Equal(["gm-chevrolet", "gm-chevrolet", "citroen"], await db.Marcas.OrderBy(m => m.Id).Select(m => m.Slug).ToListAsync());
            var modelos = await db.Modelos.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
            foreach (var modelo in modelos.Take(8)) Assert.Equal(Slugs.Base(modelo.Nome, modelo.CodigoFipe), modelo.Slug);
            Assert.Equal("107", modelos[7].Slug);
            // Repetições ganham sufixo na ordem do Id; um sufixo que já existe como nome continua único.
            Assert.Equal(["onix-1-0", "onix-1-0-2", "onix-1-0-3"], modelos.Skip(8).Take(3).Select(m => m.Slug));
            Assert.Equal(modelos.Count, modelos.Select(m => m.Slug).Distinct().Count());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
#pragma warning restore EF1002
    }
}
