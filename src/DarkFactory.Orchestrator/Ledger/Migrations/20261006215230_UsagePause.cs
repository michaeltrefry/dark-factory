using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class UsagePause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "Backoff",
                table: "controls",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "controls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResumeAt",
                table: "controls",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Backoff",
                table: "controls");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "controls");

            migrationBuilder.DropColumn(
                name: "ResumeAt",
                table: "controls");
        }
    }
}
