using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <summary>管理员默认能用聊天：系统管理员角色加上内部资料问答、公开资料问答（原来默认关、要在角色里单独勾）。
    /// 已经有的不重复加；后台里再去掉照样能关。</summary>
    public partial class AdminCanChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE role SET permissions = permissions || '[\"qa.internal\"]'::jsonb WHERE code = 'admin' AND NOT permissions ? 'qa.internal';");
            migrationBuilder.Sql("UPDATE role SET permissions = permissions || '[\"qa.public\"]'::jsonb WHERE code = 'admin' AND NOT permissions ? 'qa.public';");
            migrationBuilder.Sql("UPDATE role SET note = '问答默认开着；不想让管理员用聊天，在角色里去掉「内部资料问答」「公开资料问答」' WHERE code = 'admin' AND note LIKE '问答默认关闭%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE role SET permissions = permissions - 'qa.internal' - 'qa.public' WHERE code = 'admin';");
        }
    }
}
