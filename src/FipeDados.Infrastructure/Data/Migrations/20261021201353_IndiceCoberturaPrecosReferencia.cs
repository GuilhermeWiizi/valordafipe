using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndiceCoberturaPrecosReferencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Precos_TabelaReferenciaCodigo",
                table: "Precos");

            migrationBuilder.CreateIndex(
                name: "IX_Precos_TabelaReferenciaCodigo",
                table: "Precos",
                column: "TabelaReferenciaCodigo")
                .Annotation("Npgsql:IndexInclude", new[] { "VeiculoId", "Valor" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Precos_TabelaReferenciaCodigo",
                table: "Precos");

            migrationBuilder.CreateIndex(
                name: "IX_Precos_TabelaReferenciaCodigo",
                table: "Precos",
                column: "TabelaReferenciaCodigo");
        }
    }
}
