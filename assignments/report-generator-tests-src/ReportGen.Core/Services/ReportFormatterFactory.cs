using ReportGen.Core.Formatting;

namespace ReportGen.Core.Services;

/// <summary>
/// Factory for creating report formatters based on format name.
/// </summary>
public static class ReportFormatterFactory
{
    /// <summary>
    /// The supported format names (case-insensitive).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SupportedFormats = ["csv", "json", "markdown"];

    /// <summary>
    /// Creates a formatter for the specified format.
    /// </summary>
    /// <param name="format">The format name (case-insensitive).</param>
    /// <returns>An instance of IReportFormatter for the specified format.</returns>
    /// <exception cref="ArgumentException">Thrown when the format is not supported.</exception>
    public static IReportFormatter CreateFormatter(string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);

        return format.ToLowerInvariant() switch
        {
            "csv" => new CsvReportFormatter(),
            "json" => new JsonReportFormatter(),
            "markdown" => new MarkdownReportFormatter(),
            _ => throw new ArgumentException(
                $"Unsupported format: '{format}'. Supported formats are: {string.Join(", ", SupportedFormats)}.",
                nameof(format))
        };
    }

    /// <summary>
    /// Gets a formatted error message for unsupported format.
    /// </summary>
    public static string GetFormatErrorMessage(string format)
    {
        return $"Error: Unsupported format '{format}'. Supported formats are: {string.Join(", ", SupportedFormats)}.";
    }
}
