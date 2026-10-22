using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <summary>A Fipe devolve o zero km como "32000 Flex"; grava o rótulo de exibição "Zero KM Flex".</summary>
    [DbContext(typeof(FipeDbContext))]
    [Migration("20261022120000_RotuloAnoZeroKm")]
    public partial class RotuloAnoZeroKm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Veiculos" SET "AnoLabel" = 'Zero KM' || substr("AnoLabel", 6) WHERE "AnoLabel" LIKE '32000%';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Veiculos" SET "AnoLabel" = '32000' || substr("AnoLabel", 8) WHERE "ZeroKm" AND "AnoLabel" LIKE 'Zero KM%';""");
        }
    }
}
