using System.Net;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// sc-26314 AT1–AT5 entry points, initially exercising the shadow skeleton's real seams over loopback only.
/// These are not the terminal live campaign or a held-out evaluation: later slices extend the corresponding entry points.
/// </summary>
public class SystemOneAcceptanceTests
{
    private const string SyntheticBody = "Synthetic report: whitespace is counted as a word.";

    private sealed class DecisionEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int Calls { get; private set; }
        public bool Malformed { get; set; }
        public DecisionEndpoint()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapPost("/v1/systemone", async (HttpContext context) =>
            {
                Calls++;
                Assert.Equal("synthetic-key", context.Request.Headers["X-Weave-Router-Key"].ToString());
                Assert.False(context.Request.Headers.ContainsKey("x-weave-model-class"));
                using var request = await JsonDocument.ParseAsync(context.Request.Body);
                Assert.Equal("catalog-d3", request.RootElement.GetProperty("model").GetString());
                Assert.Contains(SyntheticBody, request.RootElement.GetProperty("state").GetString());
                return Malformed ? Results.Text("{}", "application/json") : Results.Json(new
                {
                    model = "upstream-d3",
                    answers = new { issue_kind = new { type = "choice", choice = "bug", probabilities = new { bug = .8, feature = .05, question = .05, possible_duplicate = .05, unclear = .05 }, confidence = .6 } },
                    usage = new { input_tokens = 83, output_tokens = 0 }
                });
            });
        }
        public async Task<HttpClient> StartAsync()
        {
            await _app.StartAsync(TestContext.Current.CancellationToken);
            var address = _app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
            return OutboundHttp.RouterApi(new Uri(address));
        }
        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private static FactoryOptions Options(string mode) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Issues:Classification:Mode"] = mode, ["Issues:Classification:Model"] = "catalog-d3"
    }).Build(), new NoSecrets(), _ => null);

    private static Task<IssueClassification> Classify(IssueClassifier classifier, string body = SyntheticBody) =>
        classifier.ClassifyAsync(1, IssueHashes.Version("Synthetic", body), "Synthetic", body, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AT1_mode_isolation_disabled_does_not_call_and_shadow_has_no_prompt_input()
    {
        await using var endpoint = new DecisionEndpoint();
        using var http = await endpoint.StartAsync();
        Assert.Null(IssueClassifier.Create(Options("disabled"), http, () => throw new Exception("must not read key")));
        Assert.Equal(0, endpoint.Calls);
        var issue = new IssueFacts(1, "Synthetic", SyntheticBody, "author", false, true, [], "https://example.test/issue/1", DateTimeOffset.UtcNow);
        var repo = RepoRef.Parse("example/repository");
        var before = TriagePrompt.Build(repo, issue, [repo]);
        var row = await Classify(IssueClassifier.Create(Options("shadow"), http, () => "synthetic-key")!);
        Assert.Equal("completed", row.Status);
        Assert.Equal(before, TriagePrompt.Build(repo, issue, [repo]));
        Assert.Equal(1, endpoint.Calls);
    }

    [Fact]
    public async Task AT2_authority_shadow_prediction_does_not_replace_code_aware_triage()
    {
        await using var endpoint = new DecisionEndpoint();
        using var http = await endpoint.StartAsync();
        var row = await Classify(new IssueClassifier(http, "synthetic-key", "catalog-d3"));
        Assert.Equal("bug", row.Label);
        var investigated = new Triage(IssueType.Question, "Synthetic question", "Needs explanation", ["example/repository"], .9, false, null, null);
        var route = IssueRouting.Decide(investigated, null, RepoPermission.None, [RepoRef.Parse("example/repository")], null, null, null);
        Assert.Equal(IssueRoute.Question, route.Route);
    }

    [Fact]
    public async Task AT3_failure_result_is_observable_without_response_text()
    {
        await using var endpoint = new DecisionEndpoint { Malformed = true };
        using var http = await endpoint.StartAsync();
        var row = await Classify(new IssueClassifier(http, "synthetic-key", "catalog-d3"));
        Assert.Equal("invalid_response", row.Status);
        Assert.Null(row.Label);
        Assert.DoesNotContain(SyntheticBody, JsonSerializer.Serialize(row));
    }

    [Fact]
    public void AT4_version_identity_and_durable_mapping_are_present()
    {
        var version = IssueHashes.Version("Synthetic", SyntheticBody);
        Assert.NotEqual(version, IssueHashes.Version("Synthetic", SyntheticBody + " Edited"));
        using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(FactoryOptions.DefaultConnectionString));
        var entity = db.Model.FindEntityType(typeof(IssueClassification))!;
        Assert.Equal("issue_classifications", entity.GetTableName());
        Assert.Contains(entity.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["WorkItemId", "IssueVersion"]));
        Assert.Contains(entity.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(WorkItem));
    }

    [Fact]
    public async Task AT5_measurement_inputs_preserve_usage_timing_and_confidence_separately()
    {
        await using var endpoint = new DecisionEndpoint();
        using var http = await endpoint.StartAsync();
        var row = await Classify(new IssueClassifier(http, "synthetic-key", "catalog-d3"));
        Assert.Equal(83, row.InputTokens);
        Assert.Equal(0, row.OutputTokens);
        Assert.True(row.ElapsedMs >= 0);
        Assert.Equal(.6, row.Confidence);
        Assert.Equal(.8, JsonDocument.Parse(row.Probabilities!).RootElement.GetProperty("bug").GetDouble());
        Assert.Equal("catalog-d3", row.RequestedModel);
        Assert.Equal("upstream-d3", row.Model);
        // Timing/usage are measurement inputs, not a measured speedup, calibrated accuracy, or a price estimate.
        Assert.Equal(IssueClassifier.PromptVersion, row.PromptVersion);
    }
}
