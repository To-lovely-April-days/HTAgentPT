using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <summary>聊天「＋ → 插件」只放员工会点名要做的事（做 PPT、写技术协议、出计算书……）；
    /// 材料怎么选、压力容器怎么算这类专业知识是助手自己按问题翻的，不占清单（后台技能编辑页可以再放回去）。</summary>
    public partial class SkillMenuTrim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE assistant_skill SET in_menu = false WHERE code IN ('agitation-sealing', 'batch-reactor-sim', 'chemical-safety', 'chemistry-reactions', 'control-valve', 'drawings-charts', 'equipment-selection', 'explosion-purge', 'flange-gasket-bolt', 'gas-charge', 'hazard-process', 'heat-transfer-jacket', 'instrumentation-control', 'maintenance-spares', 'material-selection', 'physical-properties', 'piping-flow', 'pressure-relief', 'pressure-vessel', 'reaction-equilibrium', 'reactor-temp-control', 'thermal-risk', 'troubleshooting');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE assistant_skill SET in_menu = true WHERE code IN ('agitation-sealing', 'batch-reactor-sim', 'chemical-safety', 'chemistry-reactions', 'control-valve', 'drawings-charts', 'equipment-selection', 'explosion-purge', 'flange-gasket-bolt', 'gas-charge', 'hazard-process', 'heat-transfer-jacket', 'instrumentation-control', 'maintenance-spares', 'material-selection', 'physical-properties', 'piping-flow', 'pressure-relief', 'pressure-vessel', 'reaction-equilibrium', 'reactor-temp-control', 'thermal-risk', 'troubleshooting');");
        }
    }
}
