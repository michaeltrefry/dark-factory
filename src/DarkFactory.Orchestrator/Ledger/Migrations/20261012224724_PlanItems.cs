using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class PlanItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PlanItemId",
                table: "work_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_items_PlanItemId",
                table: "work_items",
                column: "PlanItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_work_items_work_items_PlanItemId",
                table: "work_items",
                column: "PlanItemId",
                principalTable: "work_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_work_items_work_items_PlanItemId",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "IX_work_items_PlanItemId",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "PlanItemId",
                table: "work_items");
        }
    }
}
