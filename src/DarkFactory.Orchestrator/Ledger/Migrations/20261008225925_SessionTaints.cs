using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class SessionTaints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_taints",
                columns: table => new
                {
                    ClaudeSessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkItemId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TaintedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_taints", x => x.ClaudeSessionId);
                    table.ForeignKey(
                        name: "FK_session_taints_work_items_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "work_items",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_taints_WorkItemId",
                table: "session_taints",
                column: "WorkItemId");

            // A taint is sticky (E4): the database itself refuses to change or remove one.
            migrationBuilder.Sql("""
                CREATE FUNCTION session_taints_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'session_taints rows cannot be updated or deleted';
                END $$;
                CREATE TRIGGER session_taints_immutable BEFORE UPDATE OR DELETE ON session_taints
                    FOR EACH ROW EXECUTE FUNCTION session_taints_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_taints");

            migrationBuilder.Sql("DROP FUNCTION session_taints_immutable();");
        }
    }
}
