using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PedidosPix_UsuarioId_PlanoCodigo",
                table: "PedidosPix");

            migrationBuilder.AlterColumn<string>(
                name: "PixCopiaCola",
                table: "PedidosPix",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<bool>(
                name: "AmbienteProducao",
                table: "PedidosPix",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ChaveIdempotencia",
                table: "PedidosPix",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailPagador",
                table: "PedidosPix",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErroSincronizacao",
                table: "PedidosPix",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EstadoExterno",
                table: "PedidosPix",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoRevisao",
                table: "PedidosPix",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrdemExternaId",
                table: "PedidosPix",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PagamentoExternoId",
                table: "PedidosPix",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provedor",
                table: "PedidosPix",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProximaSincronizacaoEm",
                table: "PedidosPix",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReservaAte",
                table: "PedidosPix",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReservaSincronizacao",
                table: "PedidosPix",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RevisaoNecessaria",
                table: "PedidosPix",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SincronizadoEm",
                table: "PedidosPix",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TentativasSincronizacao",
                table: "PedidosPix",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ValorDevolvido",
                table: "PedidosPix",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "VenceEm",
                table: "PedidosPix",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendedorId",
                table: "PedidosPix",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "NotificacoesMercadoPago",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Chave = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrdemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AmbienteProducao = table.Column<bool>(type: "boolean", nullable: false),
                    VendedorId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecebidoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProximaTentativaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reserva = table.Column<Guid>(type: "uuid", nullable: true),
                    ReservaAte = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Tentativas = table.Column<int>(type: "integer", nullable: false),
                    UltimoErro = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificacoesMercadoPago", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_OrdemExternaId",
                table: "PedidosPix",
                column: "OrdemExternaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_PagamentoExternoId",
                table: "PedidosPix",
                column: "PagamentoExternoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_ProximaSincronizacaoEm",
                table: "PedidosPix",
                column: "ProximaSincronizacaoEm");

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_UsuarioId_PlanoCodigo_Provedor_AmbienteProducao_~",
                table: "PedidosPix",
                columns: new[] { "UsuarioId", "PlanoCodigo", "Provedor", "AmbienteProducao", "VendedorId" },
                unique: true,
                filter: "\"Status\" IN (0, 1, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_NotificacoesMercadoPago_Chave",
                table: "NotificacoesMercadoPago",
                column: "Chave",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificacoesMercadoPago_ProcessadoEm_ProximaTentativaEm",
                table: "NotificacoesMercadoPago",
                columns: new[] { "ProcessadoEm", "ProximaTentativaEm" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificacoesMercadoPago");

            migrationBuilder.DropIndex(
                name: "IX_PedidosPix_OrdemExternaId",
                table: "PedidosPix");

            migrationBuilder.DropIndex(
                name: "IX_PedidosPix_PagamentoExternoId",
                table: "PedidosPix");

            migrationBuilder.DropIndex(
                name: "IX_PedidosPix_ProximaSincronizacaoEm",
                table: "PedidosPix");

            migrationBuilder.DropIndex(
                name: "IX_PedidosPix_UsuarioId_PlanoCodigo_Provedor_AmbienteProducao_~",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "AmbienteProducao",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "ChaveIdempotencia",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "EmailPagador",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "ErroSincronizacao",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "EstadoExterno",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "MotivoRevisao",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "OrdemExternaId",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "PagamentoExternoId",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "Provedor",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "ProximaSincronizacaoEm",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "ReservaAte",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "ReservaSincronizacao",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "RevisaoNecessaria",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "SincronizadoEm",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "TentativasSincronizacao",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "ValorDevolvido",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "VenceEm",
                table: "PedidosPix");

            migrationBuilder.DropColumn(
                name: "VendedorId",
                table: "PedidosPix");

            migrationBuilder.AlterColumn<string>(
                name: "PixCopiaCola",
                table: "PedidosPix",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.CreateIndex(
                name: "IX_PedidosPix_UsuarioId_PlanoCodigo",
                table: "PedidosPix",
                columns: new[] { "UsuarioId", "PlanoCodigo" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }
    }
}
