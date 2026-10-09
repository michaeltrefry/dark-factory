using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// sc-25379 AC3 against real models: the spec-conformance prompt, sent by the production <see cref="RouterReviewer"/> through
/// the router to the configured spec-conformance model, flags a seeded change that adds a config flag nothing reads, and
/// the configured second model confirms it; the same flag with a consumer, against a story that asks for it (and with tests),
/// gets no blocking spec-conformance finding.
/// (GatePipelineTests covers the plumbing with a fake router; this proves the prompt.) No GitHub or Shortcut state: the
/// seeded diffs are fixtures. Live: skipped unless FACTORY_E2E=1 and FACTORY_E2E_REVIEW_SEED=1. Spends router tokens.
/// </summary>
public class ReviewSeedTests
{
    internal static readonly WorkStory Story = new(25379, "Whitespace-only input counts as zero words",
        "WordCounter.Count(\"  \") returns 1. Blank input (only spaces) must count as zero words.", "bug",
        "https://app.shortcut.com/trefry/story/25379");

    /// <summary>
    /// The story the consumed flag is reviewed against: it asks for the <c>WordCount:IgnoreBlankInput</c> setting the fixture adds,
    /// on by default, and for tests of both settings, so <c>consumed-config-flag.diff</c> is a complete, in-scope change.
    /// </summary>
    internal static readonly WorkStory ConsumedStory = new(25379, "Whitespace-only input counts as zero words, behind a setting",
        "WordCounter.Count(\"  \") returns 1. Add a `WordCount:IgnoreBlankInput` setting, on by default: when it is on, blank input "
        + "(only spaces) counts as zero words and runs of spaces do not count as extra words; when it is off, Count keeps today's "
        + "behaviour for callers that rely on it. Add tests for the default and for the setting switched off.", "bug",
        "https://app.shortcut.com/trefry/story/25379");

    private static readonly PullFacts Pull = new(1, "https://github.com/michaeltrefry/dark-factory-sandbox/pull/1", true, false, false,
        "1111111111111111111111111111111111111111", "main", "0000000000000000000000000000000000000000", null);

    private static readonly RepoFiles Files = new(
        ["README.md", "src/WordCount/WordCount.csproj", "src/WordCount/WordCountOptions.cs", "src/WordCount/WordCounter.cs", "tests/WordCountTests.cs"], false);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "review", name));

    private static async Task<RouterReviewer> RequireLiveAsync()
    {
        Harness.RequireOptIn();
        if (Environment.GetEnvironmentVariable("FACTORY_E2E_REVIEW_SEED") != "1")
        {
            Assert.Skip("Set FACTORY_E2E_REVIEW_SEED=1 to run the seeded spec-conformance review against real models.");
        }
        var routerKey = Harness.RequireSecret(o => o.RouterKey);
        Harness.RequireReviewPanel();
        await Harness.RequireRouterAsync();
        return new RouterReviewer(OutboundHttp.RouterApi(Harness.Options.RouterBaseUrl, Harness.Options.ReviewTimeout), routerKey);
    }

    private static async Task<RoleReview> SpecConformanceAsync(RouterReviewer reviewer, WorkStory story, string diff, CancellationToken ct)
    {
        var model = ReviewerChoice.Choose(Harness.Options.ReviewPanel.For(ReviewRoles.SpecConformance), "Review:SpecConformance:Models");
        var review = await reviewer.ReviewAsync(new ReviewRequest(story, "michaeltrefry/dark-factory-sandbox", Pull, diff, Files,
            ReviewRoles.SpecConformance, ReviewPrompts.For(ReviewRoles.SpecConformance), model, Guid.NewGuid().ToString()), ct);
        Assert.True(review.Clean, review.Error);
        return review;
    }

    [Fact]
    public async Task The_spec_conformance_prompt_gets_a_real_model_to_flag_an_unused_config_flag_and_a_second_model_confirms_it()
    {
        var reviewer = await RequireLiveAsync();
        var ct = TestContext.Current.CancellationToken;
        var diff = Fixture("unused-config-flag.diff");

        var review = await SpecConformanceAsync(reviewer, Story, diff, ct);

        var finding = review.Findings.FirstOrDefault(f => f.IsBlocking
            && (f.Title.Contains("IgnoreBlankInput", StringComparison.Ordinal) || f.Detail.Contains("IgnoreBlankInput", StringComparison.Ordinal)));
        if (finding is null)
        {
            Assert.Fail($"no blocking finding names IgnoreBlankInput; {review.ServedModel} found: "
                + string.Join("; ", review.Findings.Select(f => $"[{f.Severity}] {f} — {f.Detail}")));
        }

        var confirmer = ReviewerChoice.ChooseConfirmer(Harness.Options.ReviewPanel.Confirm,
            new[] { review.Model, review.ServedModel }.OfType<string>().Distinct().ToList());
        var confirmation = await reviewer.ConfirmAsync(new ConfirmRequest(Story, "michaeltrefry/dark-factory-sandbox", Pull, diff, Files,
            ReviewRoles.SpecConformance, finding, ReviewPrompts.Confirm, confirmer, Guid.NewGuid().ToString()), ct);
        Assert.True(confirmation.Outcome == Confirmation.Confirmed,
            $"{confirmation.ServedModel ?? confirmer} answered {confirmation.Outcome} on '{finding}': {confirmation.Reason}");
    }

    [Fact]
    public async Task The_same_flag_with_a_consumer_gets_no_blocking_spec_conformance_finding()
    {
        var reviewer = await RequireLiveAsync();

        var review = await SpecConformanceAsync(reviewer, ConsumedStory, Fixture("consumed-config-flag.diff"), TestContext.Current.CancellationToken);

        var blocking = review.Findings.Where(f => f.IsBlocking).ToList();
        Assert.True(blocking.Count == 0,
            $"{review.ServedModel} reported blocking findings on the consumed flag: {string.Join("; ", blocking.Select(f => $"{f} — {f.Detail}"))}");
    }
}
