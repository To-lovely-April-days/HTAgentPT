using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review",
                table: "document",
                type: "text",
                nullable: false,
                defaultValue: "Approved"); // 老数据都是管理员传的，视为已通过

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                table: "document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reviewed_at",
                table: "document",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by_id",
                table: "document",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_review_uploaded_at",
                table: "document",
                columns: new[] { "review", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_document_uploaded_by_id",
                table: "document",
                column: "uploaded_by_id");

            // 员工上传权限：已有库里的内置内部角色补上（外部客户不给）。管理员后来删掉过的不会因为这次补回——只跑一次
            migrationBuilder.Sql("""
                UPDATE role SET permissions = permissions || '["corpus.upload"]'::jsonb
                WHERE code IN ('presales', 'aftersales', 'hq_reviewer', 'admin')
                  AND NOT (permissions ? 'corpus.upload');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_review_uploaded_at",
                table: "document");

            migrationBuilder.DropIndex(
                name: "ix_document_uploaded_by_id",
                table: "document");

            migrationBuilder.DropColumn(
                name: "review",
                table: "document");

            migrationBuilder.DropColumn(
                name: "review_note",
                table: "document");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "document");

            migrationBuilder.DropColumn(
                name: "reviewed_by_id",
                table: "document");
        }
    }
}
