namespace DarkFactory.AcceptanceTests;

/// <summary>
/// The seeded review's inputs agree (not live): the story <c>ReviewSeedTests</c> sends with <c>consumed-config-flag.diff</c> asks
/// for the setting that diff adds, with the default the diff gives it, and for tests; the unused-flag story asks for no setting.
/// (<c>ReviewFixtureTests</c> in the unit tests applies the diff itself.)
/// </summary>
public class ReviewSeedStoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "review", name));

    [Fact]
    public void The_consumed_flag_story_asks_for_the_setting_the_fixture_adds_on_by_default_for_tests_and_its_readme_row()
    {
        var story = ReviewSeedTests.ConsumedStory.Description;
        var diff = Fixture("consumed-config-flag.diff");

        Assert.Contains("`WordCount:IgnoreBlankInput` setting, on by default", story);
        Assert.Contains("""+    public bool IgnoreBlankInput => config.GetValue("WordCount:IgnoreBlankInput", true);""", diff);
        Assert.Contains("Add tests for the default and for the setting switched off", story);
        Assert.Contains("+++ b/tests/WordCountTests.cs", diff);
        Assert.Contains("document the setting in the README's configuration table", story);
        Assert.Contains("+| `WordCount:IgnoreBlankInput` | `true` |", diff);
    }

    [Fact]
    public void The_unused_flag_story_asks_for_no_setting()
    {
        Assert.DoesNotContain("IgnoreBlankInput", ReviewSeedTests.Story.Description);
        Assert.DoesNotContain("setting", ReviewSeedTests.Story.Description);
    }
}
