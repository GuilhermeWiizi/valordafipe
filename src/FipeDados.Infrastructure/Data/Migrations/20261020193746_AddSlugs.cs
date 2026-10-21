using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FipeDados.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Modelos",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Marcas",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            // Preenche as linhas existentes antes dos índices únicos. Novas linhas recebem o
            // slug no SaveChanges (FipeDbContext), com o mesmo algoritmo de Slugs.Gerar.
            migrationBuilder.Sql(Preencher("Marcas", "Tipo"));
            migrationBuilder.Sql(Preencher("Modelos", "MarcaId"));

            migrationBuilder.CreateIndex(
                name: "IX_Veiculos_CodigoFipe",
                table: "Veiculos",
                column: "CodigoFipe");

            migrationBuilder.CreateIndex(
                name: "IX_Modelos_MarcaId_Slug",
                table: "Modelos",
                columns: new[] { "MarcaId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Marcas_Tipo_Slug",
                table: "Marcas",
                columns: new[] { "Tipo", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Veiculos_CodigoFipe",
                table: "Veiculos");

            migrationBuilder.DropIndex(
                name: "IX_Modelos_MarcaId_Slug",
                table: "Modelos");

            migrationBuilder.DropIndex(
                name: "IX_Marcas_Tipo_Slug",
                table: "Marcas");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Modelos");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Marcas");
        }

        // Letras latinas que a forma FormD decompõe em letra ASCII + acento (U+00C0 a U+017F),
        // geradas a partir do .NET para que o SQL reproduza Slugs.Gerar nos nomes da Fipe.
        private const string ComAcento = "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöùúûüýÿĀāĂăĄąĆćĈĉĊċČčĎďĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĨĩĪīĬĭĮįİĴĵĶķĹĺĻļĽľŃńŅņŇňŌōŎŏŐőŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽž";
        private const string SemAcento = "AAAAAACEEEEIIIINOOOOOUUUUYaaaaaaceeeeiiiinooooouuuuyyAaAaAaCcCcCcCcDdEeEeEeEeEeGgGgGgGgHhIiIiIiIiIJjKkLlLlLlNnNnNnOoOoOoRrRrRrSsSsSsSsTtTtUuUuUuUuUuUuWwYyYZzZzZz";

        /// <summary>
        /// Slug por nome dentro do grupo; repetições ganham "-2", "-3"... na ordem do Id, e nomes
        /// sem letras nem dígitos usam o código Fipe. Colisões entre um sufixo gerado e um nome
        /// que já contém esse sufixo recebem o próximo sufixo livre, preservando o menor Id.
        /// </summary>
        private static string Preencher(string tabela, string grupo) => $"""
            UPDATE "{tabela}" AS t
            SET "Slug" = CASE WHEN s.n = 1 THEN s.base ELSE s.base || '-' || s.n END
            FROM (
                SELECT "Id", base, row_number() OVER (PARTITION BY "{grupo}", base ORDER BY "Id") AS n
                FROM (
                    SELECT "Id", "{grupo}", COALESCE(NULLIF(trim(both '-' FROM regexp_replace(lower(translate(
                        replace(replace(normalize("Nome", NFC), '+', ' plus '), '&', ' e '),
                        '{ComAcento}', '{SemAcento}')), '[^a-z0-9]+', '-', 'g')), ''), "CodigoFipe"::text) AS base
                    FROM "{tabela}"
                ) AS b
            ) AS s
            WHERE t."Id" = s."Id";

            DO $slugs$
            DECLARE
                item record;
                candidato text;
                sufixo integer;
            BEGIN
                FOR item IN
                    SELECT "Id", "{grupo}" AS grupo_id, "Slug" AS slug_base
                    FROM (
                        SELECT "Id", "{grupo}", "Slug",
                            row_number() OVER (PARTITION BY "{grupo}", "Slug" ORDER BY "Id") AS ordem
                        FROM "{tabela}"
                    ) AS repetidos
                    WHERE ordem > 1
                    ORDER BY "Id"
                LOOP
                    sufixo := 2;
                    candidato := item.slug_base || '-' || sufixo;
                    WHILE EXISTS (
                        SELECT 1 FROM "{tabela}"
                        WHERE "{grupo}" = item.grupo_id AND "Slug" = candidato AND "Id" <> item."Id"
                    ) LOOP
                        sufixo := sufixo + 1;
                        candidato := item.slug_base || '-' || sufixo;
                    END LOOP;
                    UPDATE "{tabela}" SET "Slug" = candidato WHERE "Id" = item."Id";
                END LOOP;
            END
            $slugs$;
            """;
    }
}
