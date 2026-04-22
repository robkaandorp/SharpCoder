using System.Globalization;
using ReportGen.Core.Models;

namespace ReportGen.Core.Formatting;

/// <summary>
/// Formats report data as Markdown with GitHub-flavored tables.
/// </summary>
public sealed class MarkdownReportFormatter : IReportFormatter
{
    /// <inheritdoc />
    public string Format(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var writer = new StringWriter();

        // Main title
        writer.WriteLine("# Sales Report");
        writer.WriteLine();

        // Header section
        writer.WriteLine("## Header");
        writer.WriteLine();
        writer.WriteLine($"- **Title:** {data.Header.Title}");
        writer.WriteLine($"- **Generated At:** {data.Header.GeneratedAt:O}");
        writer.WriteLine();

        // Summary section
        writer.WriteLine("## Summary");
        writer.WriteLine();
        writer.WriteLine($"- **Total Revenue:** {FormatCurrency(data.Summary.TotalRevenue)}");
        writer.WriteLine($"- **Total Units Sold:** {data.Summary.TotalUnits}");
        writer.WriteLine($"- **Number of Records:** {data.Summary.RecordCount}");
        writer.WriteLine();

        // Revenue by region section
        writer.WriteLine("## Revenue by Region");
        writer.WriteLine();
        writer.WriteLine("| Region | Revenue |");
        writer.WriteLine("|--------|--------:|");
        foreach (var region in data.RevenueByRegion)
        {
            writer.WriteLine($"| {EscapeMarkdown(region.Region)} | {FormatCurrency(region.Revenue)} |");
        }
        writer.WriteLine();

        // Top products section
        writer.WriteLine("## Top 3 Products by Revenue");
        writer.WriteLine();
        writer.WriteLine("| Product | Revenue |");
        writer.WriteLine("|---------|--------:|");
        foreach (var product in data.TopProducts)
        {
            writer.WriteLine($"| {EscapeMarkdown(product.Product)} | {FormatCurrency(product.Revenue)} |");
        }

        return writer.ToString();
    }

    private static string EscapeMarkdown(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        
        // Escape pipe characters which have special meaning in markdown tables
        return value.Replace("|", "\\|");
    }

    private static string FormatCurrency(decimal value)
    {
        return value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
