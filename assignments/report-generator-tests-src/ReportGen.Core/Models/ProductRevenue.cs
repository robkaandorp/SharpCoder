namespace ReportGen.Core.Models;

/// <summary>
/// Represents revenue data for a specific product.
/// </summary>
public sealed record ProductRevenue
{
    /// <summary>
    /// The product name.
    /// </summary>
    public required string Product { get; init; }

    /// <summary>
    /// The total revenue for this product.
    /// </summary>
    public required decimal Revenue { get; init; }
}
