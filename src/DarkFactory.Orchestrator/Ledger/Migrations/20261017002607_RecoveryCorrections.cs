using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recovery_corrections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncidentId = table.Column<long>(type: "bigint", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Base = table.Column<string>(type: "text", nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    MaterialSnapshot = table.Column<string>(type: "text", nullable: false),
                    MaterialHash = table.Column<string>(type: "text", nullable: false),
                    EvidenceHash = table.Column<string>(type: "text", nullable: false),
                    Paths = table.Column<string>(type: "text", nullable: false),
                    Spec = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_corrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recovery_corrections_hot_file_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "hot_file_incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recovery_ancestors",
                columns: table => new
                {
                    CorrectionId = table.Column<int>(type: "integer", nullable: false),
                    WorkItemId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_ancestors", x => new { x.CorrectionId, x.WorkItemId, x.Scope });
                    table.ForeignKey(
                        name: "FK_recovery_ancestors_recovery_corrections_CorrectionId",
                        column: x => x.CorrectionId,
                        principalTable: "recovery_corrections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recovery_ancestors_work_items_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_ancestors_WorkItemId",
                table: "recovery_ancestors",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_corrections_IncidentId_Attempt",
                table: "recovery_corrections",
                columns: new[] { "IncidentId", "Attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_ancestors");

            migrationBuilder.DropTable(
                name: "recovery_corrections");
        }
    }
}
