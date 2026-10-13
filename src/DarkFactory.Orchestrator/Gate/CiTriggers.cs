using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// One GitHub Actions workflow file of a repository's default branch as the gate's GitHub client read it
/// (<see cref="GitHub.IGateGitHub.GetWorkflowsAsync"/>): its text, or why it could not be read.
/// </summary>
public sealed record WorkflowFile(string Path, string? Text, string? Error = null);

public enum CiTriggerOutcome
{
    /// <summary>At least one workflow would run for a plan's story pull request.</summary>
    Runs,
    /// <summary>Every workflow was read and understood, and none would run: the stories' CI can never report.</summary>
    Never,
    /// <summary>It cannot be told (no workflow file, an unreadable or unparsable one, a trigger this check does not model).</summary>
    Unknown,
}

/// <summary>
/// What <see cref="CiTriggers.Check"/> found for one repository; <see cref="Reason"/> says it in words a person can act on. Stored
/// as the <c>ci-triggers</c> checkpoint's Detail (<see cref="ToDetail"/>) when it is not <see cref="CiTriggerOutcome.Runs"/>.
/// </summary>
public sealed record CiTriggerCheck(CiTriggerOutcome Outcome, string Reason)
{
    /// <summary>How the Detail of a <c>ci-triggers</c> checkpoint that stopped the plan starts.</summary>
    public const string BlockedPrefix = "blocked: ";

    /// <summary>How the Detail of a <c>ci-triggers</c> checkpoint that is only a warning starts.</summary>
    public const string WarningPrefix = "warning: ";

    public string ToDetail() => (Outcome == CiTriggerOutcome.Never ? BlockedPrefix : WarningPrefix) + Reason;

    /// <summary>Whether a <c>ci-triggers</c> Detail records a plan stopped (<see cref="CiTriggerOutcome.Never"/>), not a warning.</summary>
    public static bool IsBlocked(string? detail) => detail?.StartsWith(BlockedPrefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// Whether a repository's CI can report on a plan's story pull requests at all (sc-25873). A plan's stories open their pull
/// requests against the plan's changeset branch (<c>factory/changeset-&lt;plan item&gt;</c>, sc-25872), and a workflow whose
/// <c>pull_request</c> trigger filters base branches (<c>branches: [main]</c>) without matching it does not run there: no check
/// ever reports, each story waits out <c>Gate:CiTimeoutMinutes</c> and escalates. That fails closed, and wastes the whole plan — so
/// it is found before the plan is spent, from the workflow files on the default branch (the branch the changeset branch starts
/// from; <c>.github/workflows/</c> is sealed, so no story changes it on the way).
/// <para>
/// A workflow reports on a story's pull request when one of its triggers fires for it: <c>pull_request</c> or
/// <c>pull_request_target</c> whose branch filter matches the base (the changeset branch; no filter matches every base), or
/// <c>push</c> whose branch filter matches the head (the story's own <c>factory/&lt;id&gt;</c> branch: a push workflow's checks are
/// on the same commit the gate reads CI for). Branch filters follow GitHub's glob rules (<see cref="Matches"/>).
/// </para>
/// The answer is only ever <see cref="CiTriggerOutcome.Never"/> when every workflow file was read, parsed and made only of triggers
/// this check knows cannot report on such a pull request. Anything else it cannot be sure of — no workflow file at all (CI may be
/// another service's, which the factory cannot read), a file that is unreadable or not valid YAML, <c>branches</c> next to
/// <c>branches-ignore</c>, a pattern it cannot translate, an event outside <see cref="NeverReports"/> — is
/// <see cref="CiTriggerOutcome.Unknown"/>: a warning, never a stop. It weakens nothing: the gate still needs green CI on every head.
/// </summary>
public static partial class CiTriggers
{
    /// <summary>Where GitHub reads workflow files from (files directly in it; subdirectories are not workflows).</summary>
    public const string Directory = ".github/workflows";

    /// <summary>The pattern a workflow's <c>pull_request</c> branch filter needs for the factory's branches, as the fix names it.</summary>
    public const string FactoryPattern = "factory/**";

    /// <summary>The most of a read error a reason repeats.</summary>
    public const int MaxErrorChars = 200;

    /// <summary>The events that can put a check on a story's pull request head, each named as GitHub does (snake_case).</summary>
    private enum Trigger
    {
        PullRequest,
        PullRequestTarget,
        Push,
    }

    private static readonly IReadOnlyDictionary<string, Trigger> Triggers = Enum.GetValues<Trigger>()
        .ToDictionary(t => Words().Replace(t.ToString(), "_$1").ToLowerInvariant(), StringComparer.Ordinal);

    [GeneratedRegex("(?<!^)([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    /// <summary>
    /// Events that cannot put a check on a new pull request's head commit: they run on a schedule or by hand, for another ref, or
    /// for the default branch's commit. An event that is neither here nor a pull request or push event makes the workflow unknown.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverReports = new HashSet<string>(StringComparer.Ordinal)
    {
        "schedule", "workflow_dispatch", "workflow_call", "repository_dispatch", "release", "merge_group", "issues", "issue_comment",
        "label", "milestone", "discussion", "discussion_comment", "fork", "watch", "gollum", "page_build", "public",
    };

    private enum Answer
    {
        Runs,
        Never,
        Unknown,
    }

    /// <summary>One workflow's answer, why in fixed words (nothing of the file's text), and the fix when a branch filter is why.</summary>
    private sealed record Workflow(string Path, Answer Answer, string Why, string? Fix = null);

    /// <summary>
    /// Whether any of <paramref name="workflows"/> (every workflow file of the default branch) would report on a pull request from
    /// <paramref name="headBranch"/> into <paramref name="baseBranch"/>.
    /// </summary>
    public static CiTriggerCheck Check(IReadOnlyList<WorkflowFile> workflows, string baseBranch, string headBranch)
    {
        if (workflows.Count == 0)
        {
            return new CiTriggerCheck(CiTriggerOutcome.Unknown,
                $"no workflow file under {Directory}/ on the default branch, so whether any CI reports on a pull request into {baseBranch} cannot be told");
        }
        var read = workflows.Select(w => Read(w, baseBranch, headBranch)).ToList();
        var files = string.Join("; ", read.Select(w => $"{Name(w.Path)} ({w.Why})"));
        if (read.FirstOrDefault(w => w.Answer == Answer.Runs) is { } runs)
        {
            return new CiTriggerCheck(CiTriggerOutcome.Runs, $"{Name(runs.Path)} runs for a pull request into {baseBranch} ({runs.Why})");
        }
        if (read.Any(w => w.Answer == Answer.Unknown))
        {
            return new CiTriggerCheck(CiTriggerOutcome.Unknown,
                $"whether any CI workflow runs for a pull request into {baseBranch} cannot be told. Workflows read on the default branch: {files}");
        }
        var fixes = read.Select(w => w.Fix).OfType<string>().ToList();
        var fix = fixes.Count > 0 ? string.Join(", or ", fixes)
            : $"add a pull_request trigger with no branches filter, or one whose branches list has `{FactoryPattern}`, to a CI workflow (e.g. {Name(read[0].Path)})";
        return new CiTriggerCheck(CiTriggerOutcome.Never,
            $"no CI workflow runs for a pull request into {baseBranch}. Workflows read on the default branch: {files}. Fix: {fix}");
    }

    private static Workflow Read(WorkflowFile file, string baseBranch, string headBranch)
    {
        if (file.Text is null)
        {
            var error = file.Error ?? "no content";
            return new Workflow(file.Path, Answer.Unknown, $"could not be read: {(error.Length > MaxErrorChars ? error[..MaxErrorChars] : error)}");
        }
        YamlNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(file.Text));
            root = stream.Documents.Count == 1 ? stream.Documents[0].RootNode : null;
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or ArgumentException or InvalidOperationException)
        {
            return new Workflow(file.Path, Answer.Unknown, "not valid YAML");
        }
        if (root is not YamlMappingNode mapping || !mapping.Children.TryGetValue(new YamlScalarNode("on"), out var on))
        {
            return new Workflow(file.Path, Answer.Unknown, "no `on:` key");
        }
        // `on: push`, `on: [push, pull_request]`, or a mapping of each event to its filters (or to nothing).
        List<(string Event, YamlNode? Config)>? events = on switch
        {
            YamlScalarNode { Value: { Length: > 0 } name } => [(name, null)],
            YamlSequenceNode list when list.Children.All(c => c is YamlScalarNode { Value.Length: > 0 }) =>
                [.. list.Children.Select(c => (((YamlScalarNode)c).Value!, (YamlNode?)null))],
            YamlMappingNode map when map.Children.Keys.All(k => k is YamlScalarNode { Value.Length: > 0 }) =>
                [.. map.Children.Select(c => (((YamlScalarNode)c.Key).Value!, (YamlNode?)c.Value))],
            _ => null,
        };
        if (events is null or { Count: 0 })
        {
            return new Workflow(file.Path, Answer.Unknown, "its `on:` could not be read");
        }
        var answers = events.Select(e => Event(file.Path, e.Event, e.Config, baseBranch, headBranch)).ToList();
        return answers.FirstOrDefault(a => a.Answer == Answer.Runs)
            ?? answers.FirstOrDefault(a => a.Answer == Answer.Unknown)
            // Of the triggers that do not fire, the one a person can fix (a branch filter) says why.
            ?? answers.FirstOrDefault(a => a.Fix is not null)
            ?? answers[0];
    }

    private static Workflow Event(string path, string name, YamlNode? config, string baseBranch, string headBranch)
    {
        if (!Triggers.TryGetValue(name, out var trigger))
        {
            return NeverReports.Contains(name)
                ? new Workflow(path, Answer.Never, "no pull_request or push trigger")
                : new Workflow(path, Answer.Unknown, "a trigger this check does not read");
        }
        // The branch a filter is matched against: a pull request's base, a push's own branch.
        var pull = trigger != Trigger.Push;
        var branch = pull ? baseBranch : headBranch;
        var filters = config switch
        {
            null or YamlScalarNode { Value: null or "" or "null" or "~" } => new YamlMappingNode(),
            YamlMappingNode map => map,
            _ => null,
        };
        if (filters is null)
        {
            return new Workflow(path, Answer.Unknown, $"its {name} trigger could not be read");
        }
        var (include, ignore) = (Child(filters, "branches"), Child(filters, "branches-ignore"));
        if (include is not null && ignore is not null)
        {
            // GitHub refuses a workflow with both.
            return new Workflow(path, Answer.Unknown, $"its {name} trigger has both branches and branches-ignore");
        }
        if (include is null && ignore is null)
        {
            // A push with only tag filters does not run for a branch.
            return !pull && (Child(filters, "tags") ?? Child(filters, "tags-ignore")) is not null
                ? new Workflow(path, Answer.Never, $"its {name} trigger is for tags only")
                : new Workflow(path, Answer.Runs, $"{name}, no branches filter");
        }
        var key = include is not null ? "branches" : "branches-ignore";
        if (Patterns((include ?? ignore)!) is not { } patterns || Matches(patterns, branch, ignoring: include is null) is not { } runs)
        {
            return new Workflow(path, Answer.Unknown, $"its {name} {key} filter could not be read");
        }
        if (runs)
        {
            return new Workflow(path, Answer.Runs, $"{name}, its {key} filter lets {branch} through");
        }
        return new Workflow(path, Answer.Never, $"its {name} {key} filter leaves out {branch}", pull
            ? include is not null
                ? $"add `{FactoryPattern}` to the {name} branches filter of {Name(path)}"
                : $"take the pattern that matches {branch} out of the {name} branches-ignore filter of {Name(path)}"
            : null);
    }

    /// <summary>
    /// Whether a workflow with this branch filter runs for <paramref name="branch"/>, by GitHub's rules; null when a pattern cannot
    /// be read. <c>branches</c> (<paramref name="ignoring"/> false): patterns apply in order, a later match overriding an earlier
    /// one — a plain pattern that matches includes the branch, one starting with <c>!</c> that matches excludes it again — and it
    /// runs when the branch ends up included. <c>branches-ignore</c>: it runs unless a pattern matches (no <c>!</c> there). In a
    /// pattern <c>*</c> is any run of characters but <c>/</c>, <c>**</c> any run at all, <c>?</c> and <c>+</c> zero-or-one and
    /// one-or-more of the character before, <c>[…]</c> a set, <c>\</c> takes the next character literally. Case-sensitive.
    /// </summary>
    public static bool? Matches(IReadOnlyList<string> patterns, string branch, bool ignoring = false)
    {
        var included = false;
        foreach (var pattern in patterns)
        {
            var negative = pattern.StartsWith('!');
            if (pattern.Length == 0 || (negative && ignoring) || Glob(negative ? pattern[1..] : pattern) is not { } glob)
            {
                return null;
            }
            if (glob.IsMatch(branch))
            {
                included = !negative;
            }
        }
        return ignoring ? !included : included;
    }

    /// <summary>A branch filter pattern as a regular expression (no backtracking: the pattern is the target repository's text); null when it is not one.</summary>
    private static Regex? Glob(string pattern)
    {
        if (pattern.Length is 0 or > 256)
        {
            return null;
        }
        var regex = new System.Text.StringBuilder("^");
        var repeatable = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    repeatable = false;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    repeatable = false;
                    break;
                case '?' or '+':
                    if (!repeatable)
                    {
                        return null;
                    }
                    regex.Append(c);
                    repeatable = false;
                    break;
                case '[':
                    var close = pattern.IndexOf(']', i + 1);
                    if (close < 0 || !SetBody().IsMatch(pattern.AsSpan(i + 1, close - i - 1)))
                    {
                        return null;
                    }
                    regex.Append(pattern, i, close - i + 1);
                    i = close;
                    repeatable = true;
                    break;
                case '\\':
                    if (++i >= pattern.Length)
                    {
                        return null;
                    }
                    regex.Append(Regex.Escape(pattern[i].ToString()));
                    repeatable = true;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    repeatable = true;
                    break;
            }
        }
        try
        {
            return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What a <c>[…]</c> set may hold to be passed to the regular expression as it is: letters, digits, ranges and a few marks.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SetBody();

    /// <summary>A filter's patterns: one string, or a list of strings; null for anything else (an empty list too).</summary>
    private static List<string>? Patterns(YamlNode node) => node switch
    {
        YamlScalarNode { Value: { Length: > 0 } one } => [one],
        YamlSequenceNode { Children.Count: > 0 } list when list.Children.All(c => c is YamlScalarNode { Value.Length: > 0 }) =>
            [.. list.Children.Select(c => ((YamlScalarNode)c).Value!)],
        _ => null,
    };

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    /// <summary>A workflow file's path as a reason shows it: the repository's own text, so bounded and plain or not shown.</summary>
    private static string Name(string path) => PlainPath().IsMatch(path) ? path : "(a workflow file with an unusual name)";

    [GeneratedRegex(@"^[A-Za-z0-9_./ -]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPath();
}
