using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <summary>解析超时默认从 300 秒改成 1800 秒：上千页的 PDF 本地 MinerU 要十来分钟。
    /// 只改还停在旧默认值 300 的；管理员自己改过的不动。</summary>
    public partial class ParseTimeoutLonger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE sys_config SET value = '\"1800\"'::jsonb, updated_at = now() WHERE key = 'parser.timeout_seconds' AND value #>> '{}' = '300';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE sys_config SET value = '\"300\"'::jsonb, updated_at = now() WHERE key = 'parser.timeout_seconds' AND value #>> '{}' = '1800';");
        }
    }
}
