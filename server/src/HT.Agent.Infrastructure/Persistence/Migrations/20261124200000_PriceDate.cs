using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HT.Agent.Infrastructure.Persistence.Migrations
{
    /// <summary>价格加日期：采购记录（金蝶、用友导出的采购订单）每一行是一次采购，记下采购日期，查价时用最近一次的价。</summary>
    public partial class PriceDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "price_date",
                table: "price_item",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_date",
                table: "price_item");
        }
    }
}
