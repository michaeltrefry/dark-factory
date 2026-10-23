using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>Validated assessment data, not a delivery grant or executable worker instruction.</summary>
public sealed record RecoveryFinding(string Kind, bool Blocking, string Issue, string ExpectedBehavior,
    IReadOnlyList<string> Tests, IReadOnlyList<string> Paths, IReadOnlyList<string> Citations, IReadOnlyList<string> PendingItems);
public sealed record PendingRecoveryDisposition(string Item, string Action, string Issue, IReadOnlyList<string> Citations);

internal static class RecoveryDecision
{
    internal static HotFileAssessment Parse(string text, HotFileSnapshot trigger, RecoverySnapshot material)
    {
        if (Encoding.UTF8.GetByteCount(text) > 32 * 1024) throw new InvalidOperationException("Assessment output exceeds 32 KiB.");
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        Fields(root, "version", "snapshot", "generation", "repo", "decision", "evidenceHash", "materialSnapshot", "citations", "findings");
        if (root.GetProperty("version").GetInt32() != 1 || Text(root, "snapshot", 200) != trigger.Id
            || root.GetProperty("generation").GetInt32() != trigger.Generation || Text(root, "repo", 200) != trigger.Repo
            || Text(root, "evidenceHash", 200) != trigger.EvidenceHash || Text(root, "materialSnapshot", 200) != material.Id)
            throw new InvalidOperationException("Assessment does not name the current incident and material snapshot.");
        var decision = Text(root, "decision", 20);
        if (decision is not ("safe" or "unsafe" or "inconclusive")) throw new InvalidOperationException("Unsupported assessment decision.");
        var evidence = material.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var citations = Citations(root.GetProperty("citations"));
        if (decision != "inconclusive" && citations.Count == 0) throw new InvalidOperationException("A decision requires evidence citations.");
        var nodes = root.GetProperty("findings");
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 10) throw new InvalidOperationException("At most 10 findings.");
        var findings = new List<RecoveryFinding>();
        foreach (var node in nodes.EnumerateArray())
        {
            Fields(node, "kind", "blocking", "issue", "expectedBehavior", "tests", "paths", "citations", "pendingItems");
            var kind = Text(node, "kind", 20);
            if (kind is not ("merged" or "pending")) throw new InvalidOperationException("Unknown finding classification.");
            var refs = Citations(node.GetProperty("citations"));
            if (refs.Count == 0) throw new InvalidOperationException("A finding requires evidence citations.");
            var pending = Strings(node.GetProperty("pendingItems"), 100, 100);
            var paths = Strings(node.GetProperty("paths"), 20, 1000);
            if (paths.Count == 0 || paths.Any(p => RepoPath.Normalize(p) != p || p.Length == 0 || p.Contains('\\')))
                throw new InvalidOperationException("Finding paths must be explicit normalized repository paths.");
            var tests = Strings(node.GetProperty("tests"), 5, 300);
            if (tests.Count == 0) throw new InvalidOperationException("A finding needs a regression-test description.");
            if (kind == "merged" && (!refs.Any(id => evidence[id].Category is "merged" or "merged-diff") || pending.Count > 0))
                throw new InvalidOperationException("An already-merged defect must cite merged evidence, not pending work alone.");
            if (kind == "pending" && (pending.Count == 0 || pending.Any(item => !refs.Any(id =>
                evidence[id].Category == "pending" && PendingItem(evidence[id]) == item))))
                throw new InvalidOperationException("A pending finding must cite each affected snapshot item.");
            findings.Add(new(kind, node.GetProperty("blocking").GetBoolean(), Text(node, "issue", 2000),
                Text(node, "expectedBehavior", 2000), tests, paths, refs, pending));
        }
        if (findings.SelectMany(f => f.Paths).Distinct().Count() > 20) throw new InvalidOperationException("Findings exceed 20 explicit paths.");
        if (decision == "safe" && findings.Any(f => f.Blocking)) throw new InvalidOperationException("Safe contradicts blocking findings.");
        if (decision == "unsafe" && !findings.Any(f => f.Blocking)) throw new InvalidOperationException("Unsafe has no actionable disposition.");
        return new(trigger.Id, trigger.Generation, trigger.Repo, "high", decision, trigger.EvidenceHash)
        {
            MaterialSnapshot = material.Id, Citations = citations, Findings = findings,
            CorrectionFindings = decision == "unsafe" ? findings.Where(f => f.Kind == "merged" && f.Blocking).ToArray() : [],
            PendingDispositions = decision == "unsafe" ? findings.Where(f => f.Kind == "pending" && f.Blocking)
                .SelectMany(f => f.PendingItems.Select(item => new PendingRecoveryDisposition(item, "hold-and-reassess", f.Issue, f.Citations))).ToArray() : []
        };

        IReadOnlyList<string> Citations(JsonElement value)
        {
            var ids = Strings(value, 100, 200);
            if (ids.Any(id => !evidence.ContainsKey(id))) throw new InvalidOperationException("Citation is not a member of this snapshot.");
            return ids;
        }
    }

    internal static string? PendingItem(RecoveryEvidence evidence)
    {
        using var doc = JsonDocument.Parse(evidence.Content);
        return doc.RootElement.GetProperty("ExternalId").GetString();
    }

    internal static void Fields(JsonElement node, params string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
            if (!names.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidOperationException("Unknown or duplicate assessment field.");
        if (seen.Count != names.Length) throw new InvalidOperationException("Missing assessment field.");
    }

    private static string Text(JsonElement node, string key, int max) => String(node.GetProperty(key), max);
    private static string String(JsonElement node, int max)
    {
        var text = node.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl))
            throw new InvalidOperationException("Assessment text is absent, oversized or contains controls.");
        return text;
    }
    private static IReadOnlyList<string> Strings(JsonElement node, int count, int length)
    {
        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() > count) throw new InvalidOperationException("Assessment list bound exceeded.");
        var values = node.EnumerateArray().Select(n => String(n, length)).ToArray();
        if (values.Distinct().Count() != values.Length) throw new InvalidOperationException("Duplicate assessment list entry.");
        return values;
    }
}
