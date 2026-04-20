using System.Globalization;
using ReportGen.Core.Models;

namespace ReportGen.Core.Formatting;

/// <summary>
/// Formats report data as CSV with clear section separators.
/// </summary>
public sealed class CsvReportFormatter : IReportFormatter
{
    /// <inheritdoc />
    public string Format(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var writer = new StringWriter();

        // Header section
        writer.WriteLine("Section,Field,Value");
        writer.WriteLine($"Header,Title,{CsvEscape(data.Header.Title)}");
        writer.WriteLine($"Header,GeneratedAt,{data.Header.GeneratedAt:O}");

        // Empty line to separate sections
        writer.WriteLine();

        // Summary section
        writer.WriteLine("Section,Field,Value");
        writer.WriteLine($"Summary,TotalRevenue,{FormatCurrency(data.Summary.TotalRevenue)}");
        writer.WriteLine($"Summary,TotalUnits,{data.Summary.TotalUnits}");
        writer.WriteLine($"Summary,RecordCount,{data.Summary.RecordCount}");

        // Empty line to separate sections
        writer.WriteLine();

        // Revenue by region section
        writer.WriteLine("Section,Region,Revenue");
        foreach (var region in data.RevenueByRegion)
        {
            writer.WriteLine($"RevenueByRegion,{CsvEscape(region.Region)},{FormatCurrency(region.Revenue)}");
        }

        // Empty line to separate sections
        writer.WriteLine();

        // Top products section
        writer.WriteLine("Section,Product,Revenue");
        foreach (var product in data.TopProducts)
        {
            writer.WriteLine($"TopProducts,{CsvEscape(product.Product)},{FormatCurrency(product.Revenue)}");
        }

        return writer.ToString();
    }

    private static string CsvEscape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Escape quotes and wrap in quotes if contains comma, newline, or quote
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static string FormatCurrency(decimal value)
    {
        return value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
