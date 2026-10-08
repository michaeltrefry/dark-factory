using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DarkFactory.Orchestrator.Ledger.Migrations
{
    /// <summary>
    /// NOTIFY session_events '&lt;WorkerSessionId&gt;:&lt;max Sequence&gt;' per session for every insert
    /// statement, delivered when its transaction commits; the <c>factory work</c> host's
    /// SessionEventRelay LISTENs and pushes the stored events to live viewers, whichever process wrote them.
    /// </summary>
    public partial class SessionEventsNotify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION notify_session_events() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('session_events', n."WorkerSessionId"::text || ':' || max(n."Sequence")::text)
                    FROM new_rows n
                    GROUP BY n."WorkerSessionId";
                    RETURN NULL;
                END
                $$;

                CREATE TRIGGER session_events_notify
                    AFTER INSERT ON session_events
                    REFERENCING NEW TABLE AS new_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_session_events();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER session_events_notify ON session_events;
                DROP FUNCTION notify_session_events();
                """);
        }
    }
}
