using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_no = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    group = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    spec = table.Column<string>(type: "text", nullable: true),
                    material = table.Column<string>(type: "text", nullable: true),
                    supplier = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    code = table.Column<string>(type: "text", nullable: true),
                    unit = table.Column<string>(type: "text", nullable: true),
                    price_unit = table.Column<string>(type: "text", nullable: true),
                    tax_low = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    tax_high = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    net_low = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    net_high = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    tax_derived = table.Column<bool>(type: "boolean", nullable: false),
                    lead_time = table.Column<string>(type: "text", nullable: true),
                    frequency = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    search_text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_table",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    sheet_name = table.Column<string>(type: "text", nullable: true),
                    column_map = table.Column<string>(type: "text", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_table", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_key = table.Column<string>(type: "text", nullable: false),
                    version_date = table.Column<DateOnly>(type: "date", nullable: false),
                    uploaded_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by_name = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    issues = table.Column<string>(type: "text", nullable: false),
                    diff = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_version", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_price_item_table_id_key",
                table: "price_item",
                columns: new[] { "table_id", "key" });

            migrationBuilder.CreateIndex(
                name: "ix_price_item_version_id",
                table: "price_item",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_table_company_id_name",
                table: "price_table",
                columns: new[] { "company_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_version_table_id",
                table: "price_version",
                column: "table_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_item");

            migrationBuilder.DropTable(
                name: "price_table");

            migrationBuilder.DropTable(
                name: "price_version");
        }
    }
}
