using System.Diagnostics;
using DarkFactory.Orchestrator.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26197: what the real Claude Code CLI does with simple-router's tier-empty answer — 429 <c>rate_limit_error</c> whose
/// message starts <c>model_class_unavailable:</c>, with <c>Retry-After</c> the earliest model reset (MessageRouter's
/// <c>WriteTierEmptyAsync</c>). Runs the <c>claude</c> on this machine's PATH (skipped where there is none, e.g. CI) through the
/// real <see cref="ClaudeWorker"/>, unsandboxed, against a stub on 127.0.0.1 with a fake router key and a throwaway Claude
/// config directory: no real endpoint, key or login is ever used. Measured on 2.1.291 (CLAUDE.md, "Worker model classes"):
/// Claude Code retries a 429 only when <c>Retry-After</c> is 60 s or less (up to <c>CLAUDE_CODE_MAX_RETRIES</c>, default 10,
/// waiting the larger of it and its own backoff); above 60 s it gives up at once. Either way it ends with an error result that
/// carries the router's message, which the worker reads as <see cref="WorkerResult.ModelClassUnavailable"/>: a usage pause,
/// never an escalation.
/// </summary>
public class RealClaudeTierEmptyTests
{
    private static readonly Lazy<string?> Claude = new(() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "claude"))
            .FirstOrDefault(File.Exists));

    private const string TierEmptyBody =
        """{"type":"error","error":{"type":"rate_limit_error","message":"model_class_unavailable: no model in the mid tier can serve this request now (usage limit, login or availability); the earliest resets at 2026-10-15 23:00:00Z."}}""";

    [Fact]
    public async Task A_tier_empty_answer_with_a_far_reset_ends_the_real_claude_session_at_once_as_model_class_unavailable()
    {
        Assert.SkipUnless(Claude.Value is not null, "claude is not on PATH");
        var dir = Directory.CreateTempSubdirectory("df-real-claude-").FullName;
        try
        {
            var requests = 0;
            var builder = WebApplication.CreateSlimBuilder();
                        await using var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.MapPost("/v1/messages", async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "3600";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(TierEmptyBody);
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            // A throwaway config directory: none of the owner's settings, plugins or login is read.
            var config = Directory.CreateDirectory(Path.Combine(dir, "config")).FullName;
            var work = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
            var wrapper = Path.Combine(dir, "claude-wrapper.sh");
            File.WriteAllText(wrapper, $"#!/bin/sh\nCLAUDE_CONFIG_DIR='{config}'\nexport CLAUDE_CONFIG_DIR\nexec '{Claude.Value}' \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var clock = Stopwatch.StartNew();
            var result = await new ClaudeWorker(wrapper, new Uri(address + "/"), "fake-key-for-stub", TimeSpan.FromMinutes(2))
                .RunAsync(work, "say hi", null, WorkerModelClass.Mid, new WorkerCallbacks(), TestContext.Current.CancellationToken);
            clock.Stop();

            Assert.False(result.Succeeded);
            Assert.True(result.ModelClassUnavailable, $"result: {result.ResultText}; stderr: {result.StderrTail}");
            Assert.True(result.UsageLimited);
            Assert.Equal(1, requests);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
