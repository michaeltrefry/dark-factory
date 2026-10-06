using System.Net;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public class StoryIdTests
{
    [Theory]
    [InlineData("sc-25172", 25172)]
    [InlineData("SC-7", 7)]
    [InlineData("123", 123)]
    [InlineData(" sc-9 ", 9)]
    public void Parses_valid_ids(string input, int expected)
    {
        Assert.True(StoryId.TryParse(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sc-")]
    [InlineData("sc-0")]
    [InlineData("story-12")]
    [InlineData("12a")]
    [InlineData("sc--5")]
    public void Rejects_invalid_ids(string input) => Assert.False(StoryId.TryParse(input, out _));

    [Fact]
    public void Branch_uses_factory_prefix() => Assert.Equal("factory/sc-42", StoryId.BranchName(42));
}

public class RepoResolverTests
{
    private static readonly RepoRef Default = new("michaeltrefry", "dark-factory-sandbox");

    [Fact]
    public void Uses_default_without_repo_line() =>
        Assert.Equal(Default, RepoResolver.Resolve("Fix the thing.\nIt mentions Repo: in passing owner/x.", Default));

    [Fact]
    public void Uses_default_for_null_description() => Assert.Equal(Default, RepoResolver.Resolve(null, Default));

    [Theory]
    [InlineData("Some text\nRepo: acme/widgets\nmore")]
    [InlineData("repo:   acme/widgets  ")]
    [InlineData("**Repo:** `acme/widgets`")]
    [InlineData("- Repo: acme/widgets")]
    public void Repo_line_overrides_default(string description) =>
        Assert.Equal(new RepoRef("acme", "widgets"), RepoResolver.Resolve(description, Default));

    [Theory]
    [InlineData("Repo: ../widgets")]
    [InlineData("Repo: acme/..")]
    [InlineData("Repo: ./widgets")]
    [InlineData("Repo: acme/.")]
    [InlineData("Repo: .hidden/widgets")]
    [InlineData("Repo: acme/.git")]
    public void Repo_line_with_dot_segments_is_rejected(string description) =>
        Assert.Throws<ArgumentException>(() => RepoResolver.Resolve(description, Default));

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("x/..")]
    [InlineData(" ../x")]
    public void Parse_rejects_dot_segments(string fullName) =>
        Assert.Throws<ArgumentException>(() => RepoRef.Parse(fullName));
}

public class ShortcutClientTests
{
    [Fact]
    public async Task Reads_story_with_token_header()
    {
        var api = new FakeApi().On("GET /api/v3/stories/42", HttpStatusCode.OK,
            """{"id":42,"name":"Fix bug","description":"desc","story_type":"bug","app_url":"https://app.shortcut.com/trefry/story/42","extra":1}""");
        var client = new ShortcutClient(api.Client("https://api.app.shortcut.com/api/v3/"), "tok-123");

        var story = await client.GetStoryAsync(42, CancellationToken.None);

        Assert.Equal(new ShortcutStory(42, "Fix bug", "desc", "bug", "https://app.shortcut.com/trefry/story/42"), story);
        Assert.Equal("tok-123", api.Requests.Single().Headers["Shortcut-Token"]);
    }

    [Fact]
    public async Task Throws_on_error_status()
    {
        var api = new FakeApi();
        var client = new ShortcutClient(api.Client("https://api.app.shortcut.com/api/v3/"), "tok");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStoryAsync(1, CancellationToken.None));
        Assert.Contains("404", ex.Message);
    }
}
