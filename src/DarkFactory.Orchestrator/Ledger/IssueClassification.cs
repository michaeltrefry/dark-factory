namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Metadata only for one optional SystemOne attempt. Never contains issue text or the raw response; neither triage's
/// approval hash nor its released specification depends on this row. Confidence is reported confidence, not accuracy.
/// </summary>
public sealed class IssueClassification
{
    public long Id { get; set; }
    public long WorkItemId { get; set; }
    public required string IssueVersion { get; set; }
    public required string Mode { get; set; }
    public required string ClassifierVersion { get; set; }
    public required string PromptVersion { get; set; }
    public required string RequestedModel { get; set; }
    public string? Model { get; set; }
    public required string Status { get; set; }
    public string? Label { get; set; }
    /// <summary>JSON object of the five known labels to their probabilities, never arbitrary response JSON.</summary>
    public string? Probabilities { get; set; }
    public double? Confidence { get; set; }
    public long ElapsedMs { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
