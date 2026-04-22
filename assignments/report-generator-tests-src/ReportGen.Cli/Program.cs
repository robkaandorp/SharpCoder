using System.CommandLine;
using ReportGen.Core.Data;
using ReportGen.Core.Formatting;
using ReportGen.Core.Reporting;
using ReportGen.Core.Services;

namespace ReportGen.Cli;

/// <summary>
/// Entry point for the reportgen CLI application.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application exit code indicating success.
    /// </summary>
    public const int SuccessExitCode = 0;

    /// <summary>
    /// Application exit code indicating an error.
    /// </summary>
    public const int ErrorExitCode = 1;

    /// <summary>
    /// Main entry point.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var formatOption = new Option<string>(
            name: "--format",
            description: "Output format (csv, json, or markdown)")
        {
            IsRequired = true
        };
        formatOption.AddAlias("-f");

        var outputOption = new Option<string?>(
            name: "--output",
            description: "Output file path (optional, writes to stdout if not specified)")
        {
            IsRequired = false
        };
        outputOption.AddAlias("-o");

        var rootCommand = new RootCommand("Sales Report Generator - Generates reports from seed data")
        {
            formatOption,
            outputOption
        };

        rootCommand.SetHandler(async (string format, string? output) =>
        {
            try
            {
                var exitCode = await RunAsync(format, output);
                Environment.ExitCode = exitCode;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Error: {ex.Message}");
                Environment.ExitCode = ErrorExitCode;
            }
        }, formatOption, outputOption);

        await rootCommand.InvokeAsync(args);
        return Environment.ExitCode;
    }

    private static async Task<int> RunAsync(string format, string? outputPath)
    {
        // Validate format and create formatter
        IReportFormatter formatter;
        try
        {
            formatter = ReportFormatterFactory.CreateFormatter(format);
        }
        catch (ArgumentException)
        {
            await Console.Error.WriteLineAsync(ReportFormatterFactory.GetFormatErrorMessage(format));
            return ErrorExitCode;
        }

        // Generate report data
        var records = SeedDataSource.GetRecords();
        var generator = new ReportGenerator(records);
        var reportData = generator.Generate();

        // Format the report
        var formattedReport = formatter.Format(reportData);

        // Output to file or stdout
        if (!string.IsNullOrEmpty(outputPath))
        {
            await File.WriteAllTextAsync(outputPath, formattedReport);
            Console.WriteLine($"Report written to: {outputPath}");
        }
        else
        {
            Console.WriteLine(formattedReport);
        }

        return SuccessExitCode;
    }
}
