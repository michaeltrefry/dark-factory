using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Refreshes",
                table: "hot_file_incidents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "recovery_assessment_attempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncidentId = table.Column<long>(type: "bigint", nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    Session = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_assessment_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recovery_assessment_attempts_hot_file_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "hot_file_incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_assessment_attempts_IncidentId_Snapshot_Number",
                table: "recovery_assessment_attempts",
                columns: new[] { "IncidentId", "Snapshot", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_assessment_attempts_Session",
                table: "recovery_assessment_attempts",
                column: "Session",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_assessment_attempts");

            migrationBuilder.DropColumn(
                name: "Refreshes",
                table: "hot_file_incidents");
        }
    }
}
