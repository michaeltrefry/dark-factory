using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class StepOutcomes : Migration
    {
        /// <summary>
        /// <see cref="Ledger.StepOutcomes.Of"/> as SQL (frozen here, as the rules stood when the column was added) over <c>ledger_entries</c> (a row's previous transition from <c>prev</c>, a <c>LAG</c> over the item's
        /// transition rows): the migration's backfill of the rows written before the outcome column. JSON details are matched on their
        /// leading keys (the serialisers write them in declaration order and escape every quote inside a string, so a value cannot fake one).
        /// </summary>
        internal const string BackfillSql = """
            WITH prev AS (
                SELECT "Id", LAG("State") OVER (PARTITION BY "WorkItemId" ORDER BY "Id") AS "From"
                FROM ledger_entries WHERE "Step" IS NULL
            )
            UPDATE ledger_entries AS e SET "Outcome" = CASE
                WHEN e."Step" IS NULL THEN CASE
                    WHEN e."State" = 'Escalated' THEN 'escalated'
                    WHEN e."State" = 'Paused' AND e."Detail" = 'needs a human' THEN 'escalated'
                    WHEN e."State" IN ('Paused', 'Cancelled') THEN 'deferred'
                    WHEN p."From" = 'Paused' THEN 'passed'
                    WHEN e."State" IN ('Fixing', 'CIHealing') THEN 'failed'
                    WHEN p."From" IN ('Fixing', 'CIHealing') AND e."Detail" LIKE '% stuck: %; nothing pushed' THEN 'failed'
                    WHEN p."From" = 'MergeGate' AND e."State" = 'CI' THEN 'failed'
                    WHEN p."From" = 'Watch' AND e."State" IN ('Intake', 'Implement') THEN 'failed'
                    WHEN p."From" IN ('CI', 'MergeGate') AND e."State" = 'Review' THEN 'gate_rejected'
                    ELSE 'passed' END
                WHEN e."Step" = 'verdict' THEN CASE WHEN e."Detail" ~ '^\{"sha":"[^"]*","verdict":"pass"' THEN 'passed' ELSE 'failed' END
                WHEN e."Step" = 'fix-progress' THEN CASE WHEN e."Detail" LIKE '%"outcome":"progress","reason":%' THEN 'passed' ELSE 'failed' END
                WHEN e."Step" = 'gate' THEN CASE WHEN e."Detail" LIKE 'Merge %' THEN 'passed' ELSE 'gate_rejected' END
                WHEN e."Step" = 'new-tests' THEN CASE
                    WHEN e."Detail" ~ '^\{"base":"[^"]*","head":"[^"]*","outcome":"pass"' THEN 'passed'
                    WHEN e."Detail" ~ '^\{"base":"[^"]*","head":"[^"]*","outcome":"(rejected|no-tests)"' THEN 'gate_rejected'
                    ELSE 'failed' END
                WHEN e."Step" IN ('ci-failure', 'merge-conflict', 'stuck', 'stuck-retry', 'worktree-lost', 'github-refused') THEN 'failed'
                WHEN e."Step" = 'escalation-comment' THEN CASE WHEN e."Detail" = 'posted' THEN 'passed' ELSE 'failed' END
                WHEN e."Step" = 'approval-ignored' THEN 'gate_rejected'
                WHEN e."Step" IN ('usage-pause', 'parked') THEN 'deferred'
                ELSE 'passed' END
            FROM ledger_entries AS r LEFT JOIN prev AS p ON p."Id" = r."Id"
            WHERE e."Id" = r."Id"
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // sc-25389: every ledger row carries its typed outcome (E7). The column is added nullable, every existing row is
            // backfilled with the outcome the writer's rules give it (Ledger.StepOutcomes.Of, as SQL: BackfillSql), and only
            // then made NOT NULL and checked against the five names, so no row is left without one and none can be written without one.
            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "ledger_entries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.AlterColumn<string>(
                name: "Outcome",
                table: "ledger_entries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ledger_entries_outcome",
                table: "ledger_entries",
                sql: "\"Outcome\" IN ('passed', 'failed', 'gate_rejected', 'deferred', 'escalated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ledger_entries_outcome",
                table: "ledger_entries");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "ledger_entries");
        }
    }
}
