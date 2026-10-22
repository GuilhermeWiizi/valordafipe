using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddColetaMensal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColetaMensalConfiguracoes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PrimeiraCompetencia = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColetaMensalConfiguracoes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ColetasMensais",
                columns: table => new
                {
                    Competencia = table.Column<DateOnly>(type: "date", nullable: false),
                    TabelaReferenciaCodigo = table.Column<int>(type: "integer", nullable: true),
                    Estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Tentativas = table.Column<int>(type: "integer", nullable: false),
                    UltimaTentativaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcluidaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VeiculosPercorridos = table.Column<int>(type: "integer", nullable: false),
                    PrecosConsultados = table.Column<int>(type: "integer", nullable: false),
                    ItensComFalha = table.Column<int>(type: "integer", nullable: false),
                    UltimoErro = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    JobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColetasMensais", x => x.Competencia);
                    table.ForeignKey(
                        name: "FK_ColetasMensais_TabelasReferencia_TabelaReferenciaCodigo",
                        column: x => x.TabelaReferenciaCodigo,
                        principalTable: "TabelasReferencia",
                        principalColumn: "Codigo");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColetasMensais_TabelaReferenciaCodigo",
                table: "ColetasMensais",
                column: "TabelaReferenciaCodigo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColetaMensalConfiguracoes");

            migrationBuilder.DropTable(
                name: "ColetasMensais");
        }
    }
}
