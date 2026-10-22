using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator.Tests;

public class ReviewOutputBudgetTests
{
    [Theory]
    [InlineData(null, 32768)]
    [InlineData("24576", 24576)]
    [InlineData("1", 1)]
    public void Budget_defaults_and_positive_integers(string? value, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Review:MaxOutputTokens"] = value }).Build();
        var options = new FactoryOptions(config, new InMemorySecrets(), _ => null);
        Assert.Equal(expected, options.ReviewMaxOutputTokens);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("words")]
    public void Invalid_budget_refuses_start_up(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Review:MaxOutputTokens"] = value }).Build();
        var options = new FactoryOptions(config, new InMemorySecrets(), _ => null);
        Assert.Contains("Review:MaxOutputTokens", Assert.Throws<InvalidOperationException>(() => options.ValidateSettings()).Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Every_tool_final_and_transient_request_has_the_budget_and_truncation_fails_closed(bool confirm, bool final)
    {
        var calls = 0;
        var api = new FakeApi().On("POST /v1/messages", _ =>
        {
            calls++;
            if (calls == 1) return SseAnswers.Response(SseAnswers.MessageStart("router-selected") + SseAnswers.Error("server_error", "transient"));
            var turn = calls - 1;
            return SseAnswers.Response(turn <= (final ? RouterReviewer.MaxTurns : 1)
                ? SseAnswers.ToolTurn(("call" + turn, "list_files", "{}"))
                : SseAnswers.Answer(confirm ? "{\"confirmed\":false}" : "{\"findings\":[]}", stop: "max_tokens"));
        });
        using var http = api.Client("http://router.test/");
        var reviewer = new RouterReviewer(http, "test-key", transientRetryDelay: TimeSpan.Zero, maxOutputTokens: 24576);
        var story = new WorkStory(77, "name", "description", "bug", "https://example.test/77", ItemNaming.Shortcut);
        var pull = new PullFacts(1, "https://github.com/o/r/pull/1", true, false, false, new string('a', 40), "main", new string('b', 40), null);
        if (confirm)
        {
            var answer = await reviewer.ConfirmAsync(new(story, "o/r", pull, "diff", new([], false), "correctness", new("blocking", "bug", null, null, "bad"), ReviewPrompts.Confirm, "session"), default);
            Assert.Equal(Confirmation.Unusable, answer.Outcome);
            Assert.Equal(ReviewFailure.OutputTruncated, answer.Failure);
            Assert.Equal("max_tokens", answer.Turns!.Last().StopReason);
        }
        else
        {
            var answer = await reviewer.ReviewAsync(new(story, "o/r", pull, "diff", new([], false), "correctness", ReviewPrompts.For("correctness"), "session"), default);
            Assert.NotNull(answer.Error);
            Assert.Equal(ReviewFailure.OutputTruncated, answer.Failure);
            Assert.Equal("max_tokens", answer.Turns!.Last().StopReason);
        }
        Assert.Equal((final ? RouterReviewer.MaxTurns : 1) + 2, calls);
        Assert.All(api.Requests, r =>
        {
            using var body = JsonDocument.Parse(r.Body!);
            Assert.Equal(24576, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("high", r.Headers["X-Weave-Model-Class"]);
        });
    }
    [Theory]
    [InlineData(null, null, 32768)]
    [InlineData(32768, null, 32768)]
    [InlineData(49152, 32768, 32768)]
    public async Task Default_configured_and_durably_pinned_budget_reach_the_wire(int? configured, int? pinned, int expected)
    {
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer("{\"findings\":[]}")));
        using var http = api.Client("http://router.test/");
        var reviewer = configured is { } limit ? new RouterReviewer(http, "test-key", maxOutputTokens: limit) : new RouterReviewer(http, "test-key");
        var story = new WorkStory(77, "name", "description", "bug", "https://example.test/77", ItemNaming.Shortcut);
        var pull = new PullFacts(1, "https://github.com/o/r/pull/1", true, false, false, new string('a', 40), "main", new string('b', 40), null);
        var review = await reviewer.ReviewAsync(new(story, "o/r", pull, "diff", new([], false), "correctness", ReviewPrompts.For("correctness"), "session", MaxOutputTokens: pinned), default);
        Assert.Null(review.Error);
        Assert.Equal(expected, Assert.Single(review.Turns!).MaxOutputTokens);
        using var body = JsonDocument.Parse(Assert.Single(api.Requests).Body!);
        Assert.Equal(expected, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

}
