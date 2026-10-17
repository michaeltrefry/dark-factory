using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>A fresh high-class assessment over immutable evidence, using the panel's bounded streaming tool loop.</summary>
public sealed class RouterHotFileAssessor(RouterReviewer reviewer, Func<HotFileSnapshot, CancellationToken, Task<string>> evidence,
    ISessionCostSource? costs = null, IReviewFiles? files = null,
    TimeSpan? costSettleDelay = null, IReadOnlyList<TimeSpan>? costRetryDelays = null,
    Func<TimeSpan, CancellationToken, Task>? costDelay = null, TimeProvider? time = null) : IHotFileAssessor
{
    public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
    {
        var material = await CaptureAsync(snapshot, ct);
        var captured = RecoverySnapshot.FromDetail(material)
            ?? throw new InvalidOperationException("Required recovery snapshot is not readable.");
        if (!captured.Complete || captured.IncidentSnapshot != snapshot.Id || captured.Repo != snapshot.Repo || captured.Generation != snapshot.Generation)
            throw new InvalidOperationException("Required recovery snapshot is incomplete or names another incident.");
        var prompt = ReviewPrompts.For("hot-file-assessment");
        // An index, not the bodies: up to 2 MiB of evidence inline would leave the tool budget at its floor and overrun the
        // context. Every entry is read by id through read_evidence, which serves exactly these hashed bodies.
        var request = JsonSerializer.Serialize(new { version = 1, snapshot.Id, snapshot.Generation, snapshot.Repo, snapshot.EvidenceHash,
            materialHash = RecoverySnapshot.Hash(material), material = Index(captured), limits = Limits });
        var promptChars = request.Length + prompt.Text.Length;
        var tools = new RecoveryAssessmentTools(captured, files, promptChars);
        var bindings = tools.Bindings;
        AssessmentConversation? conversation = null;
        HotFileAssessment Inconclusive(string why) => new(snapshot.Id, snapshot.Generation, snapshot.Repo, conversation?.Class,
            "inconclusive", snapshot.EvidenceHash) { Problem = why, MaterialSnapshot = captured.Id };
        if (Unreadable(captured, promptChars) is { } oversized)
        {
            // Decided before any router call: no session is opened for an answer that could never count.
            return Inconclusive(oversized) with { Oversized = true, Material = material, MaterialHash = RecoverySnapshot.Hash(material),
                Session = session, Prompt = prompt.Id };
        }
        HotFileAssessment parsed;
        try
        {
            conversation = await reviewer.AssessAsync(session, prompt.Text, request, bindings, ct);
            if (conversation.Class != "high" || conversation.Stop != "end_turn" || conversation.Problem is not null)
                throw new InvalidOperationException(conversation.Problem ?? "Unusable high-class assessment stream.");
            parsed = RecoveryDecision.Parse(conversation.Text, snapshot, captured);
            if (Unread(parsed, captured, tools.FullyRead) is { Count: > 0 } unread)
                parsed = Inconclusive($"A {parsed.Decision} answer rests on evidence it did not read in full: {string.Join(", ", unread)}.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            parsed = Inconclusive(ex.Message);
        }
        SessionCost? cost = null;
        if (costs is not null)
        {
            var retries = costRetryDelays ?? SessionRecorder.DefaultCostRetryDelays;
            var settle = costSettleDelay ?? SessionRecorder.DefaultCostSettleDelay;
            // Accounting is bounded separately from the conversation; its deadline may already have expired.
            using var accounting = new CancellationTokenSource(TimeSpan.FromSeconds(30) + settle
                + TimeSpan.FromTicks(retries.Sum(d => d.Ticks)), time ?? TimeProvider.System);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct, accounting.Token);
            try
            {
                await SessionCostAccounting.CollectAsync(costs, session, retries, settle,
                    costDelay ?? ((d, token) => Task.Delay(d, time ?? TimeProvider.System, token)), TextWriter.Null,
                    measured => { cost = measured; return Task.CompletedTask; }, bound.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Unknown spend is never zero spend. */ }
        }
        // Freshness gates dispositions as well as safe resolution. Revalidate after accounting, just before returning.
        if (parsed.Decision is "safe" or "unsafe")
        {
            try
            {
                if (await CaptureAsync(snapshot, ct) != material) parsed = Inconclusive("Snapshot evidence changed during assessment.") with { EvidenceChanged = true };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                parsed = Inconclusive("Snapshot evidence could not be revalidated.");
            }
        }
        return parsed with { Material = material, MaterialHash = RecoverySnapshot.Hash(material), Session = session, Prompt = prompt.Id,
            Tools = conversation?.Tools ?? [], Turns = conversation?.Turns ?? [],
            CostUsd = cost is { RequestCount: > 0 } ? cost.ActualCostUsdMicros / 1_000_000m : null };
    }

    /// <summary>The assessor's session limits, as the request states them to the model.</summary>
    internal static object Limits => new { pageBytes = RecoveryAssessmentTools.PageBytes, toolCalls = RecoveryAssessmentTools.MaxToolCalls,
        toolTurns = RecoveryAssessmentTools.MaxTurns, toolResultChars = RecoveryAssessmentTools.MaxSessionChars };

    /// <summary>
    /// Why no assessment session could judge this material safe, or null when one can (sc-26444): a safe answer must read every
    /// <c>merged-diff</c> entry in full (<see cref="Unread"/>), page by page (<see cref="RecoveryAssessmentTools.Pages"/>), and those
    /// pages alone must fit the session's tool calls (<see cref="RecoveryAssessmentTools.MaxToolCalls"/>) and its tool-result
    /// budget (<see cref="RecoveryAssessmentTools.Budget"/>, the request deducted). Decided before the router is called, so an
    /// incident too large to read in one session spends no session on it; the recovery escalates it at once with this reason
    /// (<see cref="HotFileAssessment.Oversized"/>) instead of retrying the same evidence until its attempts run out.
    /// </summary>
    internal static string? Unreadable(RecoverySnapshot material, int promptChars)
    {
        var pages = material.Evidence.Where(e => e.Category == "merged-diff").Select(RecoveryAssessmentTools.Pages).ToArray();
        var (count, chars) = (pages.Sum(p => p.Count), pages.Sum(p => (long)p.Sum(page => page.Length)));
        var budget = RecoveryAssessmentTools.Budget(promptChars);
        var over = new List<string>();
        if (count > RecoveryAssessmentTools.MaxToolCalls) over.Add($"{count} read_evidence pages exceed the assessor's {RecoveryAssessmentTools.MaxToolCalls} tool calls");
        if (chars > budget) over.Add($"{chars} characters of pages exceed the assessor's tool-result budget of {budget} characters");
        return over.Count == 0 ? null
            : $"Not assessed: a safe answer must read all {pages.Length} merged diffs in full, and {string.Join("; ", over)} for one "
                + "session. The change is too large for an autonomous assessment; a human must review it.";
    }

    /// <summary>
    /// The evidence a safe or unsafe answer must have read whole and did not: every id it cites (top level and per finding),
    /// and for a safe answer also every merged diff of the incident (the material that was in view when the bodies were
    /// inline). Read whole = a successful <c>read_evidence</c> of that id whose result reached the model uncut
    /// (<see cref="RecoveryAssessmentTools.FullyRead"/>: kept by the tool runner that produced this conversation's tool
    /// records, so it is exactly the conversation's successful, uncut reads of every page of the entry; calls past the
    /// session's caps are answered without running and count as unread). The request is an index, so a citation is no
    /// evidence the model saw the body, and a page skipped or cut by the session budget was not seen. A diff of any size is
    /// read in pages; merged diffs that together cannot fit one session are refused before it (<see cref="Unreadable"/>).
    /// </summary>
    internal static IReadOnlyList<string> Unread(HotFileAssessment answer, RecoverySnapshot material, IReadOnlySet<string> fullyRead)
    {
        if (answer.Decision is not ("safe" or "unsafe")) return [];
        var required = answer.Citations.Concat(answer.Findings.SelectMany(f => f.Citations));
        if (answer.Decision == "safe") required = required.Concat(material.Evidence.Where(e => e.Category == "merged-diff").Select(e => e.Id));
        return required.Distinct(StringComparer.Ordinal).Where(id => !fullyRead.Contains(id)).ToArray();
    }

    /// <summary>The material as the model is first shown it: identity, base tips, what is missing, and per evidence entry its id,
    /// category, provenance, commits, SHA-256, UTF-8 size and number of <c>read_evidence</c> pages — never its content.</summary>
    internal static object Index(RecoverySnapshot material) => new
    {
        material.Id, material.IncidentSnapshot, material.Repo, material.Generation, material.BaseTips, material.Missing,
        Evidence = material.Evidence.Select(e => new { e.Id, e.Category, e.Provenance, e.Commit, e.BaseCommit, e.Hash,
            Bytes = Encoding.UTF8.GetByteCount(e.Content), Pages = RecoveryAssessmentTools.Pages(e).Count }).ToArray(),
    };

    private async Task<string> CaptureAsync(HotFileSnapshot snapshot, CancellationToken ct)
    {
        var material = await evidence(snapshot, ct);
        if (Encoding.UTF8.GetByteCount(material) > RecoverySnapshot.MaxBytes)
            throw new InvalidOperationException("Required recovery evidence exceeds 2 MiB; not assessed.");
        return material;
    }
}
