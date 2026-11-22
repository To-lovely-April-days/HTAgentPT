using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHazardData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "controlled_chemical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    list = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cas = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_controlled_chemical", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hazard_chemical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    sub = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    aliases = table.Column<string>(type: "text", nullable: false),
                    english = table.Column<string>(type: "text", nullable: true),
                    cas = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    classes = table.Column<string>(type: "text", nullable: false),
                    toxic = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hazard_chemical", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hazard_process",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reaction_type = table.Column<string>(type: "text", nullable: false),
                    monitor_units = table.Column<string>(type: "text", nullable: false),
                    intro = table.Column<string>(type: "text", nullable: false),
                    hazards = table.Column<string>(type: "text", nullable: false),
                    typical = table.Column<string>(type: "text", nullable: false),
                    @params = table.Column<string>(name: "params", type: "text", nullable: false),
                    requirements = table.Column<string>(type: "text", nullable: false),
                    controls = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hazard_process", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_controlled_chemical_list_no",
                table: "controlled_chemical",
                columns: new[] { "list", "no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hazard_chemical_cas",
                table: "hazard_chemical",
                column: "cas");

            migrationBuilder.CreateIndex(
                name: "ix_hazard_chemical_seq",
                table: "hazard_chemical",
                column: "seq");

            migrationBuilder.CreateIndex(
                name: "ix_hazard_process_no",
                table: "hazard_process",
                column: "no",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "controlled_chemical");

            migrationBuilder.DropTable(
                name: "hazard_chemical");

            migrationBuilder.DropTable(
                name: "hazard_process");
        }
    }
}
