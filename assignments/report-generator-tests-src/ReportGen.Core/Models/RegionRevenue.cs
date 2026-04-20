namespace ReportGen.Core.Models;

/// <summary>
/// Represents revenue data for a specific region.
/// </summary>
public sealed record RegionRevenue
{
    /// <summary>
    /// The region name.
    /// </summary>
    public required string Region { get; init; }

    /// <summary>
    /// The total revenue for this region.
    /// </summary>
    public required decimal Revenue { get; init; }
}
