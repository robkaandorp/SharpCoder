using System.Text;
using SharpCoder.Providers.Web;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>Direct logical-line and UTF-8 renderer contracts, independent of HTTP and transforms.</summary>
public sealed class WebToolRendererTests
{
    private static readonly WebToolRenderer.SearchRow Row = new("T", "U", "C");

    [Fact]
    public void Fetch_Golden_TitleContentAndOneLink_IsExact()
    {
        Assert.Equal("Title: T\nContent: C\nLinks:\nL", Fetch("T", "C", ["L"]));
    }

    [Fact]
    public void Fetch_FinalEmptyContentLine_IsSignificantWithAndWithoutLinks()
    {
        Assert.Equal("Title: T\nContent: C\n\nLinks:\nL", Fetch("T", "C\n", ["L"]));
        Assert.Equal("Title: T\nContent: C\n", Fetch("T", "C\n", []));
    }

    [Fact]
    public void Search_Golden_BlocksHaveOneEmptySeparatorLine()
    {
        var output = WebToolRenderer.Search([Row, new WebToolRenderer.SearchRow("T2", "U2", "C2")], false, 65536);
        Assert.Equal("Title: T\nURL: U\nContent: C\n\nTitle: T2\nURL: U2\nContent: C2", output);
    }

    [Fact]
    public void Search_OmissionOnlyGolden_IsExact()
    {
        Assert.Equal("Title: T\nURL: U\nContent: C\n\nRESULTS-OMITTED",
            WebToolRenderer.Search([Row], true, 65536));
    }

    [Fact]
    public void Search_OmissionAndByteTruncation_WithNoSelectedLines_IsExactCombinedNotice()
    {
        var output = WebToolRenderer.Search([Row], true, 26);
        Assert.Equal("RESULTS-OMITTED\n\nTRUNCATED", output);
        Assert.Equal(26, Encoding.UTF8.GetByteCount(output));
        Assert.Equal(26, Encoding.UTF8.GetByteCount("RESULTS-OMITTED\n\nTRUNCATED"));
    }

    [Theory]
    [InlineData("C", new[] { "C" })]
    [InlineData("C\n", new[] { "C", "" })]
    [InlineData("", new[] { "" })]
    public void Message_EmptyAndFinalEmptyLines_UseExactJoinSemantics(string value, string[] expectedLines)
    {
        var output = WebToolRenderer.Message(value, 65536);
        Assert.Equal(string.Join("\n", expectedLines), output);
        Assert.Equal(Math.Max(0, expectedLines.Length - 1), output.Count(c => c == '\n'));
    }

    [Fact]
    public void Fields_MultilineCrlfAndLoneCr_PrefixOnlyFirstLineAndPreserveContinuation()
    {
        var output = WebToolRenderer.Fetch("T\r\nT2\rT3", "C\rC2", ["L1\r\nL2"], 65536, 100);
        Assert.Equal("Title: T\nT2\nT3\nContent: C\nC2\nLinks:\nL1\nL2", output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Fetch_MultilineTruncation_OneOrTwoLineBudget_IsNoticeAlone(int maxLines)
    {
        Assert.Equal("TRUNCATED", Fetch("T", "C", ["L"], maxLines: maxLines));
    }

    [Fact]
    public void Fetch_ThreeLineBudget_RetainsOneContentLineBlankAndTruncated()
    {
        Assert.Equal("Title: T\n\nTRUNCATED", Fetch("T", "C", ["L"], maxLines: 3));
    }

    [Fact]
    public void Fetch_ByteTruncationWithNoRetainedContent_HasNoLeadingBlank()
    {
        var output = Fetch("éééé", "C", [], displayBytes: 16);
        Assert.Equal("TRUNCATED", output);
    }

    [Fact]
    public void Fetch_FinalEmptyLineOmittedByCap_EmitsTruncated()
    {
        const string full = "Title: T\nContent: C\n";
        var output = Fetch("T", "C\n", [], displayBytes: Encoding.UTF8.GetByteCount(full) - 1);
        Assert.Equal("Title: T\n\nTRUNCATED", output);
    }

    [Fact]
    public void Utf8Cap_NeverSplitsMultibyteSequenceOrPartialLogicalLine()
    {
        var full = Fetch("é", "C", [], displayBytes: 100);
        var byteCount = Encoding.UTF8.GetByteCount(full);
        for (var cap = OllamaWebTools.MinimumDisplayBytes; cap <= byteCount + 2; cap++)
        {
            var output = Fetch("é", "C", [], displayBytes: cap);
            Assert.True(Encoding.UTF8.GetByteCount(output) <= cap);
            Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(output)), output);
            Assert.DoesNotContain("Title: ", output.Replace("Title: é", string.Empty), StringComparison.Ordinal);
        }
        Assert.Equal("Title: é\n\nTRUNCATED", Fetch("é", new string('C', 100), [], displayBytes: Encoding.UTF8.GetByteCount("Title: é\n\nTRUNCATED")));
    }

    [Fact]
    public void ExactFitWithoutTruncation_DoesNotReserveAnUnusedNotice()
    {
        var output = Fetch("é", "C", [], displayBytes: Encoding.UTF8.GetByteCount("Title: é\nContent: C"));
        Assert.Equal("Title: é\nContent: C", output);
    }

    [Fact]
    public void LiteralTable_ContainsEveryFixedLiteralAndComputesMinimumSolelyFromIt()
    {
        Assert.Contains("Unexpected tool failure.", WebToolDisplayLiterals.All);
        Assert.Contains("Tool output transformation failed.", WebToolDisplayLiterals.All);
        Assert.Contains("RESULTS-OMITTED\n\nTRUNCATED", WebToolDisplayLiterals.All);
        Assert.Equal(26, Encoding.UTF8.GetByteCount("RESULTS-OMITTED\n\nTRUNCATED"));
        Assert.Equal(WebToolDisplayLiterals.All.Max(Encoding.UTF8.GetByteCount), OllamaWebTools.MinimumDisplayBytes);
        Assert.True(OllamaWebTools.MinimumDisplayBytes >= 26);
    }

    private static string Fetch(
        string title,
        string content,
        string[] links,
        int displayBytes = 65536,
        int maxLines = 100) =>
        WebToolRenderer.Fetch(title, content, links, displayBytes, maxLines);
}
