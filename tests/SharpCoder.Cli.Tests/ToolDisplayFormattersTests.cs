using System.Globalization;
using Xunit;

namespace SharpCoder.Cli.Tests;

public class ToolDisplayFormattersTests
{
    [Theory]
    // --- sub-second: whole milliseconds, no space, no unit bleed ---
    [InlineData(0.0, "0ms")]
    [InlineData(0.4, "0ms")]
    [InlineData(1.0, "1ms")]
    [InlineData(8.0, "8ms")]
    [InlineData(12.0, "12ms")]
    [InlineData(820.0, "820ms")]
    [InlineData(999.0, "999ms")]
    [InlineData(999.6, "999ms")] // old :n0 rounding produced "1,000 ms"
    [InlineData(-5.0, "0ms")]
    // --- seconds: one decimal, trailing .0 trimmed, no space ---
    [InlineData(1000.0, "1s")]
    [InlineData(1400.0, "1.4s")]
    [InlineData(59900.0, "59.9s")]
    [InlineData(59960.0, "1m")] // rounding must not produce "60s"
    [InlineData(59999.0, "1m")]
    // --- minutes/hours: only non-zero units, no spaces, no zero padding ---
    [InlineData(60000.0, "1m")]     // was "1 m 00 s"
    [InlineData(90000.0, "1m30s")]
    [InlineData(123000.0, "2m3s")] // was "2 m 03 s"
    [InlineData(120000.0, "2m")]    // the default bash timeout
    [InlineData(900000.0, "15m")]
    [InlineData(3599000.0, "59m59s")]
    [InlineData(3599999.0, "1h")] // round up into the next unit, never "60m"
    [InlineData(3600000.0, "1h")]
    [InlineData(3900000.0, "1h5m")] // was "1 h 05 m"
    [InlineData(90000000.0, "25h")]  // was "25 h 00 m"
    public void Duration_ProducesCompactCultureInvariantText(double milliseconds, string expected)
    {
        Assert.Equal(expected, ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(milliseconds)));

        // The same value must render identically under a culture with different separators.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(milliseconds)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0L, "0ms")]
    [InlineData(1L, "1ms")]
    [InlineData(5000L, "5s")]
    [InlineData(120000L, "2m")]
    [InlineData(900000L, "15m")]
    [InlineData(3600000L, "1h")]
    [InlineData(-1L, "0ms")]
    public void Timeout_UsesTheSameCompactFormat(long milliseconds, string expected)
    {
        Assert.Equal(expected, ToolDisplayFormatters.Timeout(milliseconds));
    }

    [Fact]
    public void Duration_IsMonotonicAcrossUnitBoundaries()
    {
        // The old formatter printed "1,000 ms" then "1.0 s", and "60.0 s" then "1 m 00 s".
        // Adjacent values must never produce a longer/duplicate-looking representation.
        string[] values =
        [
            ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(999)),
            ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(1000)),
            ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(59900)),
            ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(60000)),
            ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(3599000)),
            ToolDisplayFormatters.Duration(TimeSpan.FromMilliseconds(3600000)),
        ];

        Assert.Equal(["999ms", "1s", "59.9s", "1m", "59m59s", "1h"], values);
    }

    [Fact]
    public void Duration_OmitsZeroUnits()
    {
        Assert.Equal("1m", ToolDisplayFormatters.Duration(TimeSpan.FromMinutes(1)));
        Assert.Equal("2m", ToolDisplayFormatters.Duration(TimeSpan.FromMinutes(2)));
        Assert.Equal("25h", ToolDisplayFormatters.Duration(TimeSpan.FromHours(25)));
        Assert.Equal("1h", ToolDisplayFormatters.Duration(TimeSpan.FromHours(1)));
        Assert.Equal("1h5m", ToolDisplayFormatters.Duration(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5)));
    }
}
