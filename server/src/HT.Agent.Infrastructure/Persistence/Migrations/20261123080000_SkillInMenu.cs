using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <summary>技能加「出不出现在聊天技能清单里」。介绍公司、产品目录这种问一句就会自动用上的，默认不占清单。</summary>
    public partial class SkillInMenu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "in_menu",
                table: "assistant_skill",
                type: "boolean",
                nullable: false,
                defaultValue: true);
            migrationBuilder.Sql("UPDATE assistant_skill SET in_menu = false WHERE code IN ('company-profile', 'product-catalog');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "in_menu",
                table: "assistant_skill");
        }
    }
}
