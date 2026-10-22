using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPagamentosPix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PedidosPix",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<string>(type: "text", nullable: false),
                    PlanoCodigo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PlanoNome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Meses = table.Column<int>(type: "integer", nullable: false),
                    Valor = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    Referencia = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    PixCopiaCola = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Recebedor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NomePagador = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TransacaoInformada = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InformadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TransacaoConfirmada = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ValorRecebido = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    AdministradorId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConferidoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoRecusa = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AcessoAte = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PedidosPix", x => x.Id);
                    table.CheckConstraint("CK_PedidosPix_Meses", "\"Meses\" IN (1, 3, 6, 12)");
                    table.CheckConstraint("CK_PedidosPix_Valor", "\"Valor\" > 0");
                    table.ForeignKey(
                        name: "FK_PedidosPix_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_Referencia",
                table: "PedidosPix",
                column: "Referencia",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_Status_CriadoEm",
                table: "PedidosPix",
                columns: new[] { "Status", "CriadoEm" });

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_TransacaoConfirmada",
                table: "PedidosPix",
                column: "TransacaoConfirmada",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_UsuarioId_PlanoCodigo",
                table: "PedidosPix",
                columns: new[] { "UsuarioId", "PlanoCodigo" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PedidosPix");
        }
    }
}
