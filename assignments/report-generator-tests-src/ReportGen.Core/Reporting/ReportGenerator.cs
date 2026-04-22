using ReportGen.Core.Models;

namespace ReportGen.Core.Reporting;

/// <summary>
/// Generates aggregated report data from sales records.
/// </summary>
public sealed class ReportGenerator
{
    private readonly IReadOnlyList<SaleRecord> _records;
    private readonly string _title;

    /// <summary>
    /// Creates a new report generator with the specified records and title.
    /// </summary>
    public ReportGenerator(IReadOnlyList<SaleRecord> records, string title = "Sales Report")
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _title = title ?? throw new ArgumentNullException(nameof(title));
    }

    /// <summary>
    /// Generates the complete report data.
    /// </summary>
    public ReportData Generate()
    {
        var header = GenerateHeader();
        var summary = GenerateSummary();
        var revenueByRegion = CalculateRevenueByRegion();
        var topProducts = CalculateTopProducts();

        return new ReportData
        {
            Header = header,
            Summary = summary,
            RevenueByRegion = revenueByRegion,
            TopProducts = topProducts
        };
    }

    private ReportHeader GenerateHeader()
    {
        return new ReportHeader
        {
            Title = _title,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private ReportSummary GenerateSummary()
    {
        var totalRevenue = _records.Sum(r => r.Revenue);
        var totalUnits = _records.Sum(r => r.Quantity);
        var recordCount = _records.Count;

        return new ReportSummary
        {
            TotalRevenue = totalRevenue,
            TotalUnits = totalUnits,
            RecordCount = recordCount
        };
    }

    private IReadOnlyList<RegionRevenue> CalculateRevenueByRegion()
    {
        return _records
            .GroupBy(r => r.Region)
            .Select(g => new RegionRevenue
            {
                Region = g.Key,
                Revenue = g.Sum(r => r.Revenue)
            })
            .OrderByDescending(r => r.Revenue)
            .ToList()
            .AsReadOnly();
    }

    private IReadOnlyList<ProductRevenue> CalculateTopProducts()
    {
        return _records
            .GroupBy(r => r.Product)
            .Select(g => new ProductRevenue
            {
                Product = g.Key,
                Revenue = g.Sum(r => r.Revenue)
            })
            .OrderByDescending(p => p.Revenue)
            .Take(3)
            .ToList()
            .AsReadOnly();
    }
}
