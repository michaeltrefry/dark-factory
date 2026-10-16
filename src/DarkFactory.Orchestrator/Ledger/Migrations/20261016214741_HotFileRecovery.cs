using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class HotFileRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hot_file_incidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    EvidenceHash = table.Column<string>(type: "text", nullable: false),
                    Session = table.Column<string>(type: "text", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: true),
                    Answer = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hot_file_incidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hot_file_incidents_Repo_Generation",
                table: "hot_file_incidents",
                columns: new[] { "Repo", "Generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hot_file_incidents");
        }
    }
}
