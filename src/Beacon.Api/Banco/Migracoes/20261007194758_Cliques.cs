using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Api.Banco.Migracoes
{
    /// <inheritdoc />
    public partial class Cliques : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clique",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    codigo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    momento = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    navegador = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sistema = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aparelho = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    origem = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    robo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clique", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clique_codigo_momento",
                table: "clique",
                columns: new[] { "codigo", "momento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clique");
        }
    }
}
