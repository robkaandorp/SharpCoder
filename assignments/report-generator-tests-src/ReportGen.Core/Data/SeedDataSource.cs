using ReportGen.Core.Models;

namespace ReportGen.Core.Data;

/// <summary>
/// Provides the embedded seed dataset of sales records.
/// </summary>
public static class SeedDataSource
{
    /// <summary>
    /// Gets the complete seed dataset.
    /// </summary>
    public static IReadOnlyList<SaleRecord> GetRecords()
    {
        return
        [
            new SaleRecord { Date = new DateOnly(2026, 1, 5), Region = "North", Product = "Widget", Quantity = 10, UnitPrice = 9.99m },
            new SaleRecord { Date = new DateOnly(2026, 1, 7), Region = "South", Product = "Widget", Quantity = 4, UnitPrice = 9.99m },
            new SaleRecord { Date = new DateOnly(2026, 1, 9), Region = "North", Product = "Gadget", Quantity = 2, UnitPrice = 24.50m },
            new SaleRecord { Date = new DateOnly(2026, 1, 12), Region = "East", Product = "Gizmo", Quantity = 7, UnitPrice = 14.00m },
            new SaleRecord { Date = new DateOnly(2026, 1, 14), Region = "West", Product = "Widget", Quantity = 12, UnitPrice = 9.99m },
            new SaleRecord { Date = new DateOnly(2026, 1, 18), Region = "South", Product = "Gadget", Quantity = 5, UnitPrice = 24.50m },
            new SaleRecord { Date = new DateOnly(2026, 1, 21), Region = "East", Product = "Contraption", Quantity = 1, UnitPrice = 99.00m },
            new SaleRecord { Date = new DateOnly(2026, 1, 25), Region = "North", Product = "Gizmo", Quantity = 3, UnitPrice = 14.00m },
            new SaleRecord { Date = new DateOnly(2026, 1, 28), Region = "West", Product = "Gadget", Quantity = 6, UnitPrice = 24.50m },
            new SaleRecord { Date = new DateOnly(2026, 1, 30), Region = "South", Product = "Contraption", Quantity = 2, UnitPrice = 99.00m }
        ];
    }
}
