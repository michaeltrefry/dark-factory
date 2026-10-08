using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <summary>
    /// NOTIFY work_items '&lt;WorkItemId&gt;' whenever an item row (state, checkpoint, title/repo) or one of
    /// its worker_sessions rows (start, end, cost) is written, delivered when the transaction commits;
    /// the <c>factory work</c> host's SessionEventRelay LISTENs and refreshes the dashboard's pipeline view,
    /// whichever process wrote the change.
    /// </summary>
    public partial class WorkItemsNotify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION notify_work_items() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_TABLE_NAME = 'work_items' THEN
                        PERFORM pg_notify('work_items', NEW."Id"::text);
                    ELSE
                        PERFORM pg_notify('work_items', NEW."WorkItemId"::text);
                    END IF;
                    RETURN NULL;
                END
                $$;

                CREATE TRIGGER work_items_notify
                    AFTER INSERT OR UPDATE ON work_items
                    FOR EACH ROW EXECUTE FUNCTION notify_work_items();

                CREATE TRIGGER worker_sessions_notify
                    AFTER INSERT OR UPDATE ON worker_sessions
                    FOR EACH ROW EXECUTE FUNCTION notify_work_items();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER worker_sessions_notify ON worker_sessions;
                DROP TRIGGER work_items_notify ON work_items;
                DROP FUNCTION notify_work_items();
                """);
        }
    }
}
