namespace ReportGen.Core.Models;

/// <summary>
/// Aggregated report data that drives all output formats.
/// </summary>
public sealed record ReportData
{
    /// <summary>
    /// The report header containing title and timestamp.
    /// </summary>
    public required ReportHeader Header { get; init; }

    /// <summary>
    /// The report summary with totals.
    /// </summary>
    public required ReportSummary Summary { get; init; }

    /// <summary>
    /// Revenue breakdown by region, sorted by revenue descending.
    /// </summary>
    public required IReadOnlyList<RegionRevenue> RevenueByRegion { get; init; }

    /// <summary>
    /// Top 3 products by revenue, sorted descending.
    /// </summary>
    public required IReadOnlyList<ProductRevenue> TopProducts { get; init; }
}
