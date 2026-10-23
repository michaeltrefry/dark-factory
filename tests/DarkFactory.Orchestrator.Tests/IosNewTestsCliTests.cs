using System.CommandLine;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

public sealed class IosNewTestsCliTests
{
    [Theory]
    [InlineData("pass", 0)]
    [InlineData("error", 1)]
    [InlineData("rejected", 1)]
    public async Task The_owner_command_calls_the_remote_seam_and_returns_the_typed_outcome(string outcome, int expected)
    {
        IosNewTestsRequest? received = null;
        var command = IosNewTestsCli.Build((repo, request, ct) => {
            Assert.Equal("acme/ios", repo.FullName);
            received = request;
            return Task.FromResult(NewTestsResult.Without(request.Base, request.Head, outcome, "test"));
        });
        var root = new RootCommand { command };
        var result = await root.Parse(["ios-new-tests", "run", "acme/ios", new string('1', 40), new string('2', 40), "TallyTests/TallyTests/testNew"]).InvokeAsync();
        Assert.Equal(expected, result);
        Assert.Equal(["TallyTests/TallyTests/testNew"], received!.Tests);
    }
}
