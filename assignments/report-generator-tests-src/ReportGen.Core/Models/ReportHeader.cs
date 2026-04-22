namespace ReportGen.Core.Models;

/// <summary>
/// Represents the header section of a report.
/// </summary>
public sealed record ReportHeader
{
    /// <summary>
    /// The report title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// The timestamp when the report was generated (UTC, ISO-8601).
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}
