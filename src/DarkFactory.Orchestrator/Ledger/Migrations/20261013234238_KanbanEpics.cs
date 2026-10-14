using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class KanbanEpics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "kanban_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "kanban_epics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EpicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kanban_epics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kanban_epics_EpicId",
                table: "kanban_epics",
                column: "EpicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kanban_epics");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "kanban_items");
        }
    }
}
