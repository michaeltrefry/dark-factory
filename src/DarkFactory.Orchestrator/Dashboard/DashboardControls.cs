using DarkFactory.Orchestrator.Controls;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>
/// The dashboard's control form post: Pause, Continue or Stop at one scope. Like every endpoint it needs the
/// login (fallback policy), and it needs the antiforgery token; these are its only writes (E8).
/// </summary>
public static class DashboardControls
{
    public const string Path = "/controls";

    public static readonly string[] Actions = ["pause", "continue", "stop"];

    public const string By = "dashboard";

    public static void MapDashboardControls(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, async (ControlActions controls, [FromForm] string? action, [FromForm] string? scope, CancellationToken ct) =>
            {
                if (scope is null || !ControlScope.IsValid(scope) || action is null || !Actions.Contains(action))
                {
                    return Results.BadRequest();
                }
                var result = action switch
                {
                    "pause" => await controls.PauseAsync(scope, By, ct),
                    "continue" => await controls.ContinueAsync(scope, By, ct),
                    _ => await controls.StopAsync(scope, By, ct),
                };
                Console.WriteLine($"[dashboard] {action} {scope}: {result.Message}");
                // The pipeline view shows the new control state (and a stop that did not finish stays Stopping).
                return Results.Redirect("/");
            })
            .WithMetadata(new RequireAntiforgeryTokenAttribute());
}
