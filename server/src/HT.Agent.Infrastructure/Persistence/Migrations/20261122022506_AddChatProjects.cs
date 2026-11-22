using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "qa_session",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "chat_attachment",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_project",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    ledger_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_project", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_qa_session_project_id",
                table: "qa_session",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_attachment_project_id",
                table: "chat_attachment",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_project_owner_id_updated_at",
                table: "chat_project",
                columns: new[] { "owner_id", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_project");

            migrationBuilder.DropIndex(
                name: "ix_qa_session_project_id",
                table: "qa_session");

            migrationBuilder.DropIndex(
                name: "ix_chat_attachment_project_id",
                table: "chat_attachment");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "qa_session");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "chat_attachment");
        }
    }
}
