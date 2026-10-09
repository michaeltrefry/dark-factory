namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The seeded review fixtures themselves (sc-25391): <c>consumed-config-flag.diff</c> must be a clean, complete change a careful
/// reviewer passes — it applies to the WordCount files it was written against, reads the flag it adds, keeps the default the
/// story asks for (on) and adds tests for both settings. A live run once failed because the fixture, not the prompt, was wrong.
/// </summary>
public class ReviewFixtureTests
{
    /// <summary>The WordCount files the fixture diffs are written against (the seeded repo's base).</summary>
    private static readonly Dictionary<string, string> Base = new()
    {
        ["src/WordCount/WordCountOptions.cs"] = """
            using Microsoft.Extensions.Configuration;

            namespace WordCount;

            /// <summary>Settings read from the <c>WordCount</c> configuration section.</summary>
            public sealed class WordCountOptions(IConfiguration config)
            {
                public bool CountHyphenatedAsOne => config.GetValue("WordCount:CountHyphenatedAsOne", true);
            }

            """,
        ["src/WordCount/WordCounter.cs"] = """
            namespace WordCount;

            public static class WordCounter
            {
                /// <summary>Counts the space-separated words in <paramref name="text"/>.</summary>
                public static int Count(string text, WordCountOptions options)
                {
                    return text.Split(' ').Length;
                }
            }

            """,
        ["tests/WordCountTests.cs"] = """
            using Microsoft.Extensions.Configuration;
            using WordCount;
            using Xunit;

            public class WordCountTests
            {
                private static WordCountOptions Options(params (string Key, string Value)[] settings) =>
                    new(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build());

                [Fact]
                public void Counts_space_separated_words() => Assert.Equal(3, WordCounter.Count("one two three", Options()));
            }

            """,
    };

    [Fact]
    public void The_consumed_config_flag_fixture_applies_cleanly_and_adds_the_flag_its_consumer_and_tests_for_both_settings()
    {
        var dir = Directory.CreateTempSubdirectory("df-review-fixture-").FullName;
        try
        {
            foreach (var (path, text) in Base)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
                File.WriteAllText(Path.Combine(dir, path), text);
            }
            TestGit.Run(dir, "init", "-q");

            TestGit.Run(dir, "apply", "--whitespace=error-all", Path.Combine(AppContext.BaseDirectory, "Fixtures", "review", "consumed-config-flag.diff"));

            var options = File.ReadAllText(Path.Combine(dir, "src/WordCount/WordCountOptions.cs"));
            var counter = File.ReadAllText(Path.Combine(dir, "src/WordCount/WordCounter.cs"));
            var tests = File.ReadAllText(Path.Combine(dir, "tests/WordCountTests.cs"));
            // The flag, on by default as the story asks.
            Assert.Contains("""public bool IgnoreBlankInput => config.GetValue("WordCount:IgnoreBlankInput", true);""", options);
            // Its consumer: Count reads it.
            Assert.Contains("return options.IgnoreBlankInput", counter);
            Assert.Contains("StringSplitOptions.RemoveEmptyEntries", counter);
            // Tests for the default (blank input is zero words) and for the setting switched off.
            Assert.Contains("public void Blank_input_counts_as_zero_words_by_default", tests);
            Assert.Contains("""Options(("WordCount:IgnoreBlankInput", "false"))""", tests);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
