using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Marcas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    CodigoFipe = table.Column<int>(type: "integer", nullable: false),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Marcas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TabelasReferencia",
                columns: table => new
                {
                    Codigo = table.Column<int>(type: "integer", nullable: false),
                    Mes = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Data = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabelasReferencia", x => x.Codigo);
                });

            migrationBuilder.CreateTable(
                name: "Modelos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MarcaId = table.Column<int>(type: "integer", nullable: false),
                    CodigoFipe = table.Column<int>(type: "integer", nullable: false),
                    Nome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Modelos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Modelos_Marcas_MarcaId",
                        column: x => x.MarcaId,
                        principalTable: "Marcas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Veiculos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModeloId = table.Column<int>(type: "integer", nullable: false),
                    AnoModeloCodigo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Ano = table.Column<int>(type: "integer", nullable: true),
                    ZeroKm = table.Column<bool>(type: "boolean", nullable: false),
                    Combustivel = table.Column<int>(type: "integer", nullable: false),
                    AnoLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CodigoFipe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Veiculos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Veiculos_Modelos_ModeloId",
                        column: x => x.ModeloId,
                        principalTable: "Modelos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Precos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VeiculoId = table.Column<int>(type: "integer", nullable: false),
                    TabelaReferenciaCodigo = table.Column<int>(type: "integer", nullable: false),
                    Valor = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    ColetadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Precos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Precos_TabelasReferencia_TabelaReferenciaCodigo",
                        column: x => x.TabelaReferenciaCodigo,
                        principalTable: "TabelasReferencia",
                        principalColumn: "Codigo",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Precos_Veiculos_VeiculoId",
                        column: x => x.VeiculoId,
                        principalTable: "Veiculos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Marcas_Tipo_CodigoFipe",
                table: "Marcas",
                columns: new[] { "Tipo", "CodigoFipe" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Modelos_MarcaId_CodigoFipe",
                table: "Modelos",
                columns: new[] { "MarcaId", "CodigoFipe" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Precos_TabelaReferenciaCodigo",
                table: "Precos",
                column: "TabelaReferenciaCodigo");

            migrationBuilder.CreateIndex(
                name: "IX_Precos_VeiculoId_TabelaReferenciaCodigo",
                table: "Precos",
                columns: new[] { "VeiculoId", "TabelaReferenciaCodigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TabelasReferencia_Data",
                table: "TabelasReferencia",
                column: "Data");

            migrationBuilder.CreateIndex(
                name: "IX_Veiculos_ModeloId_AnoModeloCodigo",
                table: "Veiculos",
                columns: new[] { "ModeloId", "AnoModeloCodigo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Precos");

            migrationBuilder.DropTable(
                name: "TabelasReferencia");

            migrationBuilder.DropTable(
                name: "Veiculos");

            migrationBuilder.DropTable(
                name: "Modelos");

            migrationBuilder.DropTable(
                name: "Marcas");
        }
    }
}
