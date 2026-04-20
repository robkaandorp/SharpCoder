namespace ReportGen.Core.Models;

/// <summary>
/// Represents a single sales transaction.
/// </summary>
public sealed record SaleRecord
{
    /// <summary>
    /// The date of the sale.
    /// </summary>
    public required DateOnly Date { get; init; }

    /// <summary>
    /// The region where the sale occurred.
    /// </summary>
    public required string Region { get; init; }

    /// <summary>
    /// The product name.
    /// </summary>
    public required string Product { get; init; }

    /// <summary>
    /// The quantity sold.
    /// </summary>
    public required int Quantity { get; init; }

    /// <summary>
    /// The unit price.
    /// </summary>
    public required decimal UnitPrice { get; init; }

    /// <summary>
    /// Calculates the revenue for this record (Quantity × UnitPrice).
    /// </summary>
    public decimal Revenue => Quantity * UnitPrice;
}
