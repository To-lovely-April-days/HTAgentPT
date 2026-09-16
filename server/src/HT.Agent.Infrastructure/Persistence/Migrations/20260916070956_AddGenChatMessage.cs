using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenChatMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "chat_state",
                table: "generation_session",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gen_chat_message",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gen_chat_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_gen_chat_message__generation_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "generation_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gen_chat_message_session_id_id",
                table: "gen_chat_message",
                columns: new[] { "session_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gen_chat_message");

            migrationBuilder.DropColumn(
                name: "chat_state",
                table: "generation_session");
        }
    }
}
