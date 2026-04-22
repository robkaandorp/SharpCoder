using ReportGen.Core.Models;

namespace ReportGen.Core.Formatting;

/// <summary>
/// Defines a contract for formatting report data into a specific output format.
/// </summary>
public interface IReportFormatter
{
    /// <summary>
    /// Formats the report data into a string representation.
    /// </summary>
    /// <param name="data">The report data to format.</param>
    /// <returns>The formatted report as a string.</returns>
    string Format(ReportData data);
}
