using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <summary>思考档位的「回答最长」默认改成 0（不另设上限，按上下文里剩下的给）：档位的本体是模型自带的思考强度，
    /// 原来 2000 / 6000 / 12000 的上限太小，思考加回答一起算，查了几次资料后常常写到一半就停。
    /// 只改还停在旧默认值的；管理员自己改过的不动。</summary>
    public partial class LevelAnswerByContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE sys_config SET value = '"0"'::jsonb, updated_at = now()
                WHERE (key = 'assistant.level.fast.max_tokens' AND value #>> '{}' = '2000')
                   OR (key = 'assistant.level.standard.max_tokens' AND value #>> '{}' = '6000')
                   OR (key = 'assistant.level.deep.max_tokens' AND value #>> '{}' = '12000');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE sys_config SET value = CASE key
                    WHEN 'assistant.level.fast.max_tokens' THEN '"2000"'::jsonb
                    WHEN 'assistant.level.standard.max_tokens' THEN '"6000"'::jsonb
                    ELSE '"12000"'::jsonb END, updated_at = now()
                WHERE key IN ('assistant.level.fast.max_tokens', 'assistant.level.standard.max_tokens', 'assistant.level.deep.max_tokens')
                  AND value #>> '{}' = '0';
                """);
        }
    }
}
