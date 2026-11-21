using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "answer_issue",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    notify = table.Column<bool>(type: "boolean", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    resolve_note = table.Column<string>(type: "text", nullable: true),
                    resolved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    eval_question_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answer_issue", x => x.id);
                    table.ForeignKey(
                        name: "fk_answer_issue__qa_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "qa_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_question",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    expect_docs = table.Column<string>(type: "jsonb", nullable: false),
                    expect_projects = table.Column<string>(type: "jsonb", nullable: false),
                    expect_points = table.Column<string>(type: "jsonb", nullable: false),
                    expect_ask = table.Column<bool>(type: "boolean", nullable: false),
                    dept = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_question", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    finished = table.Column<int>(type: "integer", nullable: false),
                    retrieval_total = table.Column<int>(type: "integer", nullable: false),
                    retrieval_ok = table.Column<int>(type: "integer", nullable: false),
                    answer_total = table.Column<int>(type: "integer", nullable: false),
                    answer_ok = table.Column<int>(type: "integer", nullable: false),
                    run_as = table.Column<string>(type: "text", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: true),
                    changes = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_result",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retrieval_ok = table.Column<bool>(type: "boolean", nullable: true),
                    answer_ok = table.Column<bool>(type: "boolean", nullable: true),
                    ask_ok = table.Column<bool>(type: "boolean", nullable: false),
                    missing_points = table.Column<string>(type: "jsonb", nullable: false),
                    found_docs = table.Column<string>(type: "jsonb", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    seconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_result", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_result__eval_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "eval_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_eval_result_eval_question_question_id",
                        column: x => x.question_id,
                        principalTable: "eval_question",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_answer_issue_company_id_status_created_at",
                table: "answer_issue",
                columns: new[] { "company_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_answer_issue_message_id_source",
                table: "answer_issue",
                columns: new[] { "message_id", "source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_answer_issue_reporter_id_status",
                table: "answer_issue",
                columns: new[] { "reporter_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_question_company_id_code",
                table: "eval_question",
                columns: new[] { "company_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_result_question_id",
                table: "eval_result",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_result_run_id_question_id",
                table: "eval_result",
                columns: new[] { "run_id", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_run_company_id_started_at",
                table: "eval_run",
                columns: new[] { "company_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_issue");

            migrationBuilder.DropTable(
                name: "eval_result");

            migrationBuilder.DropTable(
                name: "eval_run");

            migrationBuilder.DropTable(
                name: "eval_question");
        }
    }
}
