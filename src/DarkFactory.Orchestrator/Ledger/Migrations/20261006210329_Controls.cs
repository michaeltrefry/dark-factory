using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <summary>The Pause/Continue/Stop controls table, and each work item's epic (the scope of an epic control).</summary>
    public partial class Controls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EpicId",
                table: "work_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "controls",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_controls", x => x.Scope);
                });

            // Any control change: NOTIFY work_items with an empty payload ("anything may have changed"), so the
            // dashboard's pipeline view redraws paused/stopping state at once, whichever process wrote it.
            migrationBuilder.Sql("""
                CREATE FUNCTION notify_controls() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('work_items', '');
                    RETURN NULL;
                END
                $$;

                CREATE TRIGGER controls_notify
                    AFTER INSERT OR UPDATE OR DELETE ON controls
                    FOR EACH ROW EXECUTE FUNCTION notify_controls();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER controls_notify ON controls;
                DROP FUNCTION notify_controls();
                """);

            migrationBuilder.DropTable(
                name: "controls");

            migrationBuilder.DropColumn(
                name: "EpicId",
                table: "work_items");
        }
    }
}
