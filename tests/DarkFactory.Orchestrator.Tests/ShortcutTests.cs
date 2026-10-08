using DarkFactory.Orchestrator.Shortcut;

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
