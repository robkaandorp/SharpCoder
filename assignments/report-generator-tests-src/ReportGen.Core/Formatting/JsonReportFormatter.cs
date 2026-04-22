using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReportGen.Core.Models;

namespace ReportGen.Core.Formatting;

/// <summary>
/// Formats report data as JSON with camelCase field names.
/// </summary>
public sealed class JsonReportFormatter : IReportFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new DecimalConverter() }
    };

    /// <inheritdoc />
    public string Format(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Serialize(data, Options);
    }

    /// <summary>
    /// Custom converter for decimal values to ensure 2 decimal places in invariant culture.
    /// </summary>
    private sealed class DecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetDecimal();
            }

            if (reader.TokenType == JsonTokenType.String && 
                decimal.TryParse(reader.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            throw new JsonException("Unable to convert to decimal.");
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            // Write as number, but ensure consistent formatting
            writer.WriteNumberValue(value);
        }
    }
}
