using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleStateMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Step",
                table: "ledger_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // The skeleton's Failed state is the lifecycle's Escalated.
            migrationBuilder.Sql("""UPDATE work_items SET "State" = 'Escalated' WHERE "State" = 'Failed';""");
            migrationBuilder.Sql("""UPDATE ledger_entries SET "State" = 'Escalated' WHERE "State" = 'Failed';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE work_items SET "State" = 'Failed' WHERE "State" NOT IN ('Intake', 'Implement', 'Review');""");
            migrationBuilder.Sql("""UPDATE ledger_entries SET "State" = 'Failed' WHERE "State" NOT IN ('Intake', 'Implement', 'Review');""");
            migrationBuilder.DropColumn(
                name: "Step",
                table: "ledger_entries");
        }
    }
}
