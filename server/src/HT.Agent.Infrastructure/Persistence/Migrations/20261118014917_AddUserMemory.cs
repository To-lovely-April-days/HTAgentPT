using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "memory_off",
                table: "app_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "user_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_memory", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_memory_user_id_created_at",
                table: "user_memory",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_memory");

            migrationBuilder.DropColumn(
                name: "memory_off",
                table: "app_user");
        }
    }
}
