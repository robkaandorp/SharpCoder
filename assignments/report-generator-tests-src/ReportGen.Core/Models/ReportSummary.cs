namespace ReportGen.Core.Models;

/// <summary>
/// Represents the summary section of a report.
/// </summary>
public sealed record ReportSummary
{
    /// <summary>
    /// The total revenue across all records.
    /// </summary>
    public required decimal TotalRevenue { get; init; }

    /// <summary>
    /// The total units sold across all records.
    /// </summary>
    public required int TotalUnits { get; init; }

    /// <summary>
    /// The number of records in the report.
    /// </summary>
    public required int RecordCount { get; init; }
}
