using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRankingsMensais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RankingsMensais",
                columns: table => new
                {
                    TabelaReferenciaCodigo = table.Column<int>(type: "integer", nullable: false),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    Referencia = table.Column<DateOnly>(type: "date", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Versao = table.Column<Guid>(type: "uuid", nullable: false),
                    FonteHash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Conteudo = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankingsMensais", x => new { x.TabelaReferenciaCodigo, x.Tipo });
                    table.ForeignKey(
                        name: "FK_RankingsMensais_TabelasReferencia_TabelaReferenciaCodigo",
                        column: x => x.TabelaReferenciaCodigo,
                        principalTable: "TabelasReferencia",
                        principalColumn: "Codigo",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RankingsMensais");
        }
    }
}
