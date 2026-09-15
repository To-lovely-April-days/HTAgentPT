using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "translation_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_file_name = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    term_domain = table.Column<string>(type: "text", nullable: true),
                    output_file_key = table.Column<string>(type: "text", nullable: false),
                    report = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_translation_task", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_translation_task_user_id_created_at",
                table: "translation_task",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "translation_task");
        }
    }
}
