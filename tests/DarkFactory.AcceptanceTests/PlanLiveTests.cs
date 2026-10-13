using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Not live (sc-25877): what Phase 4a's live plan tests decide with (<see cref="PlanLive"/>) and replay
/// (<see cref="StubClaude.Answering"/>) works in-process, so a live run never passes vacuously nor fails on its own harness.
/// </summary>
public class PlanLiveTests
{
    private static readonly RepoRef Up = new("acme", "up");
    private static readonly RepoRef Down = new("acme", "down");

    private static JsonObject Story(string key, params string[] blockedBy) => new()
    {
        ["key"] = key,
        ["title"] = $"Story {key}",
        ["description"] = "Something.",
        ["acceptance"] = new JsonArray("it is observable"),
        ["type"] = "chore",
        ["blocked_by"] = new JsonArray([.. blockedBy.Select(b => (JsonNode)b)]),
    };

    private static Plan Parse(params JsonObject[] stories) =>
        PlanParser.Parse("```json\n" + new JsonObject { ["stories"] = new JsonArray([.. stories]) }.ToJsonString() + "\n```");

    [Fact]
    public void P4_AT1_is_conclusive_only_for_a_plan_of_three_or_more_stories_with_a_chain_of_three()
    {
        // a ← b ← c: the chain the epic asks for (alone, and with an independent fourth story beside it).
        Assert.Null(PlanLive.ChainProblem(Parse(Story("a"), Story("b", "a"), Story("c", "b"))));
        Assert.Null(PlanLive.ChainProblem(Parse(Story("d"), Story("a"), Story("b", "a"), Story("c", "b"))));

        // Two stories, however they depend: too few.
        var two = PlanLive.ChainProblem(Parse(Story("a"), Story("b", "a")));
        Assert.StartsWith($"{PlanLive.Inconclusive}: the planner made 2 stories; P4-AT1 needs at least 3", two);
        // Three stories with no chain of three: independent ones, or two that each need only the first.
        Assert.StartsWith($"{PlanLive.Inconclusive}: no story of the plan is blocked by a story that itself has a blocker",
            PlanLive.ChainProblem(Parse(Story("a"), Story("b"), Story("c"))));
        var fan = PlanLive.ChainProblem(Parse(Story("a"), Story("b", "a"), Story("c", "a")));
        Assert.StartsWith($"{PlanLive.Inconclusive}: no story of the plan is blocked by a story that itself has a blocker", fan);
        Assert.Contains("b <- [a]", fan);
    }

    [Fact]
    public void P4_AT2_is_conclusive_only_for_a_plan_with_a_story_that_waits_on_a_changeset()
    {
        Plan Two(bool waits)
        {
            var up = Story("a");
            up["repo"] = Up.FullName;
            var down = Story("b");
            down["repo"] = Down.FullName;
            if (waits)
            {
                down["blocked_by_changesets"] = new JsonArray(Up.FullName);
            }
            var root = new JsonObject
            {
                ["stories"] = new JsonArray(up, down),
                ["merge_order"] = new JsonArray(Up.FullName, Down.FullName),
                ["interactions"] = "down consumes what up releases",
            };
            return PlanParser.Parse("```json\n" + root.ToJsonString() + "\n```", [Up, Down]);
        }

        Assert.Null(PlanLive.ChangesetBlockerProblem(Two(waits: true)));
        Assert.StartsWith($"{PlanLive.Inconclusive}: no story of the plan waits on a repository's changeset", PlanLive.ChangesetBlockerProblem(Two(waits: false)));
    }

    [Fact]
    public void The_fixed_answers_of_P4_AT3_are_refused_by_the_plan_rules_for_the_reason_each_test_expects()
    {
        var cycle = Assert.Throws<PlanFormatException>(() => PlanParser.Parse(PlanLive.CyclicAnswer));
        Assert.Contains("the stories block each other in a cycle", cycle.Message);

        var oversized = Assert.Throws<PlanFormatException>(() => PlanParser.Parse(PlanLive.OversizedAnswer));
        Assert.Equal($"the plan has {PlanParser.MaxStories + 1} stories; at most {PlanParser.MaxStories} are allowed", oversized.Message);
    }

    [Fact]
    public async Task The_stub_cli_answers_the_fixed_answer_as_a_planner_sessions_result_under_a_fresh_session_id()
    {
        var answer = PlanLive.CyclicAnswer;
        using var stub = StubClaude.Answering(answer);
        var seat = Directory.CreateTempSubdirectory("df-e2e-plan-seat-").FullName;
        try
        {
            // As the planner is launched: read-only tools confined to its seat, with the pause hook's settings.
            var args = ClaudeWorker.BuildArguments("plan the story", null, ClaudeWorker.BuildPauseSettings(Path.Combine(seat, "none.pause")), WorkerTools.ReadOnly, seat);
            var psi = new ProcessStartInfo("/bin/bash", [stub.Script, .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(process.ExitCode == 0, await stderr);
            var lines = (await stdout).TrimEnd('\n').Split('\n').Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(["system", "assistant", "result"], lines.Select(l => l.GetProperty("type").GetString()));
            var run = Assert.Single(stub.Runs());
            Assert.Equal(StubClaude.Completed, run.Ending);
            // One session id on every line, the run's own (not the transcript's).
            Assert.All(lines, l => Assert.Equal(run.Session, l.GetProperty("session_id").GetString()));
            Assert.True(Guid.TryParse(run.Session, out _));
            Assert.False(lines[2].GetProperty("is_error").GetBoolean());
            // The result is the answer, character for character: what the pipeline hands to PlanParser.
            Assert.Equal(answer, lines[2].GetProperty("result").GetString());
            Assert.Equal(answer, lines[1].GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Throws<PlanFormatException>(() => PlanParser.Parse(lines[2].GetProperty("result").GetString()));
        }
        finally
        {
            Directory.Delete(seat, recursive: true);
        }
    }
}
