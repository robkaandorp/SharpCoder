using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;
using SharpCoder;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

public class BashToolsTests
{
    private const int ConstructorDefaultTimeoutMs = 120000;
    private const int FifteenMinutesMs = 900000;

    /// <summary>Chat client that records the ChatOptions it was called with.</summary>
    private sealed class OptionsCapturingClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Installs the internal timing seam so the effective per-call timeout can be observed
    /// without ever waiting for it. The returned list records every timeout the production
    /// execution path selected, in call order.
    /// </summary>
    internal static List<int> ObserveTimeouts(BashTools tools)
    {
        var observed = new List<int>();
        tools.DelayFactory = (timeoutMs, token) =>
        {
            lock (observed) observed.Add(timeoutMs);
            // Never completes on its own: the process-exit race always wins.
            return Task.Delay(Timeout.Infinite, token);
        };
        return observed;
    }

    private static string LongRunningCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ========================================================================
    // Existing behavior guard
    // ========================================================================

    [Fact]
    public async Task ExecuteBashCommand_Echo_ReturnsOutput()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command("echo Hello XUnit", Ct);

        Assert.Contains("Hello XUnit", result);
        Assert.Contains("--- STDOUT ---", result);
    }

    // ========================================================================
    // Per-stream output cap
    // ========================================================================

    /// <summary>Marker line that closes the retained head of a capped stream.</summary>
    private const string HeadMarker = "--- OUTPUT CAPPED: HEAD ---";

    /// <summary>Marker line that opens the retained tail of a capped stream.</summary>
    private const string TailMarker = "--- OUTPUT CAPPED: TAIL ---";

    /// <summary>
    /// Fixed allowance for the transcript furniture (headers, exit-code line, markers and the
    /// omission line) that is added around a capped stream.
    /// </summary>
    private const int TranscriptAllowanceChars = 1024;

    [Fact]
    public void MaxStreamOutputChars_IsExactlyFiftyThousand_AndACompileTimeConstant()
    {
        // Read the production constant through reflection so the assertion binds to the real value
        // and kind at run time rather than being folded into this assembly at compile time.
        var field = typeof(BashTools).GetField(
            "MaxStreamOutputChars",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        Assert.NotNull(field);
        Assert.True(field!.IsLiteral, "MaxStreamOutputChars must remain a compile-time constant.");
        Assert.Equal(typeof(int), field.FieldType);
        Assert.Equal(50_000, Assert.IsType<int>(field.GetRawConstantValue()));
    }

    [Fact]
    public void CapStreamOutput_BelowCap_ReturnsTheSameStringInstance()
    {
        var text = string.Concat(Enumerable.Repeat("short line of text\n", 10));
        Assert.True(text.Length < BashTools.MaxStreamOutputChars);

        var result = BashTools.CapStreamOutput(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void CapStreamOutput_ExactlyAtCap_ReturnsTheSameStringInstance()
    {
        var text = new string('x', BashTools.MaxStreamOutputChars);

        var result = BashTools.CapStreamOutput(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void CapStreamOutput_CapPlusOne_CapsWithHeadTailMarkersAndExactCounts()
    {
        // 5,000 complete rows of 10 characters (50,000 characters) plus one extra character, so the
        // total is exactly the cap plus one and every count asserted below is a literal.
        var text = string.Concat(Enumerable.Repeat("012345678\n", 5_000)) + "Z";
        Assert.Equal(BashTools.MaxStreamOutputChars + 1, text.Length);

        var result = BashTools.CapStreamOutput(text);

        // Head: the first half of the cap, exactly; the head ends on a newline, so the marker follows
        // immediately. The omitted middle is one character; the tail is the remaining 25,000
        // characters, ending with the stream's final character.
        Assert.Equal(25_000, result.IndexOf(HeadMarker, StringComparison.Ordinal));
        Assert.EndsWith(text.Substring(25_001), result);

        // One omission line between the two markers, with the exact omitted, total-character and
        // total-line counts, and the redirect-then-read guidance.
        Assert.Contains(
            "--- OUTPUT OMITTED: 1 of 50001 characters were removed from the middle of this stream " +
            "(total lines: 5001). Redirect the output to a file and read it with grep, tail/head, " +
            "or read_file with offset/limit. ---",
            result);

        // Head marker, then the omission line, then the tail marker: one marker block in the middle.
        var headMarker = result.IndexOf(HeadMarker, StringComparison.Ordinal);
        var omission = result.IndexOf("OUTPUT OMITTED", StringComparison.Ordinal);
        var tailMarker = result.IndexOf(TailMarker, StringComparison.Ordinal);
        Assert.True(headMarker < omission, "The head marker must precede the omission line.");
        Assert.True(omission < tailMarker, "The omission line must precede the tail marker.");
    }

    [Fact]
    public void CapStreamOutput_SurrogatePairStraddlingTheHeadBoundary_IsNotSplit()
    {
        // 1,000 characters of overshoot. The 😀 pair is placed so that its high surrogate is the last
        // character of the naive head (index cap/2 - 1) and its low surrogate is the first character
        // after it, i.e. exactly across the head boundary.
        const int overshoot = 1_000;
        var pair = "\uD83D\uDE00";
        var text = new string('a', 24_999) + pair + new string('b', 25_999);
        Assert.Equal(BashTools.MaxStreamOutputChars + overshoot, text.Length);

        var result = BashTools.CapStreamOutput(text);

        // The cap engaged, and the boundary was moved by one character so the pair belongs wholly to
        // the omitted middle: the omitted count is 1,001 rather than the naive 1,000, and neither
        // half of the pair survives in a retained half.
        Assert.Contains("OUTPUT OMITTED: 1001 of 51000", result);
        Assert.DoesNotContain(pair, result);
        AssertNoLoneSurrogate(result, "the head boundary");
    }

    [Fact]
    public void CapStreamOutput_SurrogatePairStraddlingTheTailBoundary_IsNotSplit()
    {
        // 1,000 characters of overshoot. The 😀 pair is placed so that its low surrogate is the naive
        // start of the tail (index length - cap/2) and its high surrogate is the character before it,
        // i.e. exactly across the tail boundary.
        const int overshoot = 1_000;
        var pair = "\uD83D\uDE00";
        var text = new string('a', 25_999) + pair + new string('b', 24_999);
        Assert.Equal(BashTools.MaxStreamOutputChars + overshoot, text.Length);

        var result = BashTools.CapStreamOutput(text);

        // The cap engaged, and the boundary was moved by one character so the pair belongs wholly to
        // the omitted middle: the omitted count is 1,001 rather than the naive 1,000, and neither
        // half of the pair survives in a retained half.
        Assert.Contains("OUTPUT OMITTED: 1001 of 51000", result);
        Assert.DoesNotContain(pair, result);
        AssertNoLoneSurrogate(result, "the tail boundary");
    }

    /// <summary>
    /// Asserts that <paramref name="text"/> is well-formed UTF-16: no high surrogate without its
    /// low surrogate, and no unpaired low surrogate anywhere. The strict UTF-8 round trip is a
    /// second, independent check: an encoder with <c>throwOnInvalidBytes</c> faults on a lone
    /// surrogate rather than silently replacing it.
    /// </summary>
    private static void AssertNoLoneSurrogate(string text, string scenario)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                    $"{scenario}: lone high surrogate at index {i}.");
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                Assert.Fail($"{scenario}: lone low surrogate at index {i}.");
            }
        }

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bytes = strictUtf8.GetBytes(text);
        Assert.Equal(text, strictUtf8.GetString(bytes));
    }

    /// <summary>
    /// Platform-appropriate command printing a unique first line, <paramref name="bodyLines"/>
    /// body lines and a unique last line to standard output. Well over 8,000 lines, so the stream
    /// exceeds <see cref="BashTools.MaxStreamOutputChars"/> on every supported platform.
    /// </summary>
    private static string FloodStdoutCommand(int bodyLines) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"echo OUT-FIRST-LINE && (for /L %i in (1,1,{bodyLines}) do @echo OUTBODY-%i) && echo OUT-LAST-LINE"
            : $"echo OUT-FIRST-LINE; for i in $(seq 1 {bodyLines}); do echo OUTBODY-$i; done; echo OUT-LAST-LINE";

    /// <summary>
    /// Platform-appropriate command flooding <em>both</em> streams independently: a unique first and
    /// last line plus body lines on stdout, and the same shape on stderr.
    /// </summary>
    private static string FloodBothStreamsCommand(int bodyLines) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"echo OUT-FIRST-LINE && (for /L %i in (1,1,{bodyLines}) do @echo OUTBODY-%i) && echo OUT-LAST-LINE " +
              $"&& echo ERR-FIRST-LINE 1>&2 && (for /L %j in (1,1,{bodyLines}) do @echo ERRBODY-%j 1>&2) && echo ERR-LAST-LINE 1>&2"
            : $"echo OUT-FIRST-LINE; for i in $(seq 1 {bodyLines}); do echo OUTBODY-$i; done; echo OUT-LAST-LINE; " +
              $"echo ERR-FIRST-LINE >&2; for i in $(seq 1 {bodyLines}); do echo ERRBODY-$i >&2; done; echo ERR-LAST-LINE >&2";

    /// <summary>Returns the transcript text between one section header and the next.</summary>
    private static string TranscriptSection(string transcript, string header, string nextMarker)
    {
        var start = transcript.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Header '{header}' was not found in the transcript.");
        start += header.Length;

        var end = transcript.IndexOf(nextMarker, StringComparison.Ordinal);
        Assert.True(end > start, $"Marker '{nextMarker}' was not found after header '{header}'.");

        return transcript.Substring(start, end - start);
    }

    [Fact]
    public async Task ExecuteBashCommand_OversizedStdout_IsCappedKeepingFirstAndLastLines()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command(FloodStdoutCommand(8_000), Ct);

        // The command really did produce far more than the cap.
        Assert.True(
            result.Length > 0 && result.Length <= BashTools.MaxStreamOutputChars + TranscriptAllowanceChars,
            $"The transcript length {result.Length} must be bounded by the {BashTools.MaxStreamOutputChars}-character stream cap.");

        // The unique first line survives in the head, the unique last line survives in the tail, and
        // the middle is replaced by one explicit marker block.
        Assert.Contains("OUT-FIRST-LINE", result);
        Assert.Contains("OUT-LAST-LINE", result);
        Assert.Contains(HeadMarker, result);
        Assert.Contains(TailMarker, result);
        Assert.Contains("OUTPUT OMITTED", result);
        Assert.Contains("grep", result);
        Assert.Contains("read_file with offset/limit", result);
        Assert.True(
            result.IndexOf("OUT-FIRST-LINE", StringComparison.Ordinal) < result.IndexOf(HeadMarker, StringComparison.Ordinal),
            "The first line belongs to the retained head.");
        Assert.True(
            result.IndexOf("OUT-LAST-LINE", StringComparison.Ordinal) > result.IndexOf(TailMarker, StringComparison.Ordinal),
            "The last line belongs to the retained tail.");

        // Transcript furniture is added after capping and is never cut.
        Assert.Contains("--- STDOUT ---", result);
        Assert.Contains("--- EXIT CODE: 0 ---", result);
    }

    [Fact]
    public async Task ExecuteBashCommand_OversizedStreams_AreCappedOneStreamAtATime()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command(FloodBothStreamsCommand(8_000), Ct);

        Assert.Contains("--- EXIT CODE: 0 ---", result);

        var stdoutSection = TranscriptSection(result, "--- STDOUT ---", "--- STDERR ---");
        var stderrSection = TranscriptSection(result, "--- STDERR ---", "--- EXIT CODE:");

        // Both streams individually exceed the cap, and each one is capped on its own: its own
        // first/last lines, its own head/tail markers and its own omission line.
        Assert.Contains("OUT-FIRST-LINE", stdoutSection);
        Assert.Contains("OUT-LAST-LINE", stdoutSection);
        Assert.Contains(HeadMarker, stdoutSection);
        Assert.Contains(TailMarker, stdoutSection);
        Assert.Contains("OUTPUT OMITTED", stdoutSection);
        Assert.True(
            stdoutSection.Length <= BashTools.MaxStreamOutputChars + TranscriptAllowanceChars,
            $"The stdout section length {stdoutSection.Length} must be bounded by the {BashTools.MaxStreamOutputChars}-character stream cap.");

        Assert.Contains("ERR-FIRST-LINE", stderrSection);
        Assert.Contains("ERR-LAST-LINE", stderrSection);
        Assert.Contains(HeadMarker, stderrSection);
        Assert.Contains(TailMarker, stderrSection);
        Assert.Contains("OUTPUT OMITTED", stderrSection);
        Assert.True(
            stderrSection.Length <= BashTools.MaxStreamOutputChars + TranscriptAllowanceChars,
            $"The stderr section length {stderrSection.Length} must be bounded by the {BashTools.MaxStreamOutputChars}-character stream cap.");

        // Per-stream separation: neither stream's body leaks into the other's section.
        Assert.DoesNotContain("ERRBODY-", stdoutSection);
        Assert.DoesNotContain("OUTBODY-", stderrSection);
    }

    [Fact]
    public void InterruptedTranscript_IsCappedOnBothStreams_AndKeepsHeadlineAndCleanupDiagnosticsUncut()
    {
        // The timeout/ordinary-error builder is private, so it is driven directly through reflection
        // with fully captured oversized tasks. That makes the interrupted path deterministic: it does
        // not depend on how much of a killed process' output happened to reach the pipes.
        var stdoutTask = Task.FromResult(string.Concat(Enumerable.Repeat("OUTBODY-0123\n", 4_000)));
        var stderrTask = Task.FromResult(string.Concat(Enumerable.Repeat("ERRBODY-0123\n", 4_000)));
        var report = new CleanupReportForTest
        {
            OutputComplete = false,
            RootExitObserved = true,
            DegradedReason = "simulated-degraded-cleanup"
        };

        var builder = typeof(BashTools).GetMethod(
            "BuildInterruptedResult",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(builder);

        var result = Assert.IsType<string>(builder!.Invoke(
            null,
            new object?[]
            {
                "Command timed out after 60000ms.",
                stdoutTask,
                stderrTask,
                report.Instance
            }));

        // Both captured streams are capped, not just stdout, and the transcript stays bounded.
        Assert.True(
            result.Length <= (2 * BashTools.MaxStreamOutputChars) + TranscriptAllowanceChars,
            $"The interrupted transcript length {result.Length} must be bounded by two capped streams.");
        Assert.Contains("OUTBODY-", result);
        Assert.Contains("ERRBODY-", result);

        var stdoutSection = TranscriptSection(result, "--- STDOUT ---", "--- STDERR ---");
        var stderrSection = TranscriptSection(result, "--- STDERR ---", "--- CLEANUP DIAGNOSTICS ---");
        foreach (var section in new[] { stdoutSection, stderrSection })
        {
            Assert.Contains(HeadMarker, section);
            Assert.Contains(TailMarker, section);
            Assert.Contains("OUTPUT OMITTED: 2000 of 52000", section);
            Assert.Contains("total lines: 4000", section);
            Assert.Contains("grep", section);
            Assert.Contains("read_file with offset/limit", section);
        }

        // The headline and the cleanup diagnostics are added after capping and are never cut, and no
        // exit code is invented on an interrupted path.
        Assert.StartsWith("Command timed out after 60000ms.", result);
        Assert.Contains("--- CLEANUP DIAGNOSTICS ---", result);
        Assert.Contains("Descendant cleanup was NOT established: simulated-degraded-cleanup.", result);
        Assert.Contains("Output capture is INCOMPLETE", result);
        Assert.DoesNotContain("--- EXIT CODE:", result);
    }

    /// <summary>
    /// Creates a production <see cref="BashTools.CleanupReport"/> through reflection and forwards the
    /// property assignments to it, so the interrupted-path test can hand the private transcript
    /// builder a truthfully degraded report without widening production visibility.
    /// </summary>
    private sealed class CleanupReportForTest
    {
        private readonly object _instance = Activator.CreateInstance(
            typeof(BashTools).GetNestedType("CleanupReport", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("BashTools.CleanupReport was not found."))!;

        public object Instance => _instance;

        public bool OutputComplete
        {
            set => Set(nameof(OutputComplete), value);
        }

        public bool RootExitObserved
        {
            set => Set(nameof(RootExitObserved), value);
        }

        public string DegradedReason
        {
            set => Set(nameof(DegradedReason), value);
        }

        private void Set(string property, object value)
        {
            var info = _instance.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(info);
            info!.SetValue(_instance, value);
        }
    }

    // ========================================================================
    // Timeout selection
    // ========================================================================

    [Fact]
    public async Task OmittedTimeout_UsesInstanceDefault()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        var result = await tools.execute_bash_command("echo omitted", Ct);

        Assert.Contains("omitted", result);
        Assert.Equal(new[] { 4321 }, observed);
    }

    [Fact]
    public async Task NullTimeout_UsesInstanceDefault()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo null", Ct, null);

        Assert.Equal(new[] { 4321 }, observed);
    }

    [Fact]
    public async Task OmittedTimeout_WithConstructorDefault_UsesTwoMinuteDefault()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo default", Ct);

        Assert.Equal(new[] { ConstructorDefaultTimeoutMs }, observed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5000)]
    public async Task NonPositiveConstructorTimeout_FallsBackToTwoMinuteDefault(int constructorTimeout)
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: constructorTimeout);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo fallback", Ct);

        Assert.Equal(new[] { ConstructorDefaultTimeoutMs }, observed);
    }

    [Fact]
    public async Task ShorterOverride_IsSelectedForThatInvocation()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 60000);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo shorter", Ct, 5000);

        Assert.Equal(new[] { 5000 }, observed);
    }

    [Fact]
    public async Task LongerOverride_SelectsFifteenMinuteBudget()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo longer", Ct, FifteenMinutesMs);

        Assert.Equal(new[] { FifteenMinutesMs }, observed);
    }

    [Fact]
    public async Task Override_IsNotRetained_ForSubsequentInvocations()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 7000);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo one", Ct, FifteenMinutesMs);
        await tools.execute_bash_command("echo two", Ct);
        await tools.execute_bash_command("echo three", Ct, 1500);
        await tools.execute_bash_command("echo four", Ct, null);

        Assert.Equal(new[] { FifteenMinutesMs, 7000, 1500, 7000 }, observed);
    }

    [Fact]
    public async Task EffectiveTimeout_IsUsedInTheTimeoutMessage()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var observed = new List<int>();
        tools.DelayFactory = (timeoutMs, _) =>
        {
            observed.Add(timeoutMs);
            return Task.CompletedTask; // force the timeout branch immediately
        };

        var result = await tools.execute_bash_command(LongRunningCommand(), Ct, FifteenMinutesMs);

        Assert.Equal(new[] { FifteenMinutesMs }, observed);
        Assert.Contains($"timed out after {FifteenMinutesMs}ms", result);
    }

    // ========================================================================
    // Invalid values are rejected before launching a process
    // ========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-900000)]
    public async Task InvalidTimeout_ThrowsBeforeProcessLaunch(int timeoutMs)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "bashtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var marker = Path.Combine(workDir, "marker.txt");
            var tools = new BashTools(workDir);
            var observed = ObserveTimeouts(tools);

            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => tools.execute_bash_command("echo launched > marker.txt", Ct, timeoutMs));

            Assert.Equal("timeout_ms", ex.ParamName);
            Assert.False(File.Exists(marker), "No process may be launched for an invalid timeout.");
            Assert.Empty(observed);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // ========================================================================
    // Pre-cancelled calls launch nothing (lifecycle fix)
    // ========================================================================

    [Fact]
    public async Task PreCancelledToken_ThrowsBeforeProcessLaunch()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "bashtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            var marker = Path.Combine(workDir, "marker.txt");
            var tools = new BashTools(workDir);
            var observed = ObserveTimeouts(tools);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.execute_bash_command("echo launched > marker.txt", cts.Token));

            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.False(File.Exists(marker), "No process may be launched for an already-cancelled call.");
            Assert.Empty(observed);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidTimeout_IsRejected_EvenWhenTheTokenIsAlreadyCancelled(int timeoutMs)
    {
        // Argument validation still runs first: the contract for an invalid timeout_ms is
        // unchanged by the pre-cancellation guard.
        var tools = new BashTools(Environment.CurrentDirectory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tools.execute_bash_command("echo never", cts.Token, timeoutMs));

        Assert.Equal("timeout_ms", ex.ParamName);
    }

    // ========================================================================
    // Source/binary compatibility of the original signature
    // ========================================================================

    [Fact]
    public async Task OriginalSignature_StillResolves_WithSingleArgumentAndPositionalToken()
    {
        var tools = new BashTools(Environment.CurrentDirectory);

        // One-argument call (token defaulted) and the existing positional-token call
        // must both remain unambiguous. The whole point of this test is the *shape* of
        // these legacy call sites, so the xUnit1051 "pass TestContext token" rule is
        // suppressed narrowly here; the token-passing form is covered on the next line.
#pragma warning disable xUnit1051
        var single = await tools.execute_bash_command("echo single");
        var positional = await tools.execute_bash_command("echo positional", default);
#pragma warning restore xUnit1051
        var withTestToken = await tools.execute_bash_command("echo token", Ct);

        Assert.Contains("single", single);
        Assert.Contains("positional", positional);
        Assert.Contains("token", withTestToken);
    }

    [Fact]
    public async Task TwoParameterDelegate_BindsToOriginalSignature()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        Func<string, CancellationToken, Task<string>> del = tools.execute_bash_command;
        var result = await del("echo delegate", Ct);

        Assert.Contains("delegate", result);
        Assert.Equal(new[] { 4321 }, observed);
    }

    [Fact]
    public async Task ThreeParameterDelegate_BindsToTimeoutAwareOverload()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        Func<string, CancellationToken, int?, Task<string>> del = tools.execute_bash_command;
        var result = await del("echo delegate3", Ct, 6000);

        Assert.Contains("delegate3", result);
        Assert.Equal(new[] { 6000 }, observed);
    }

    // ========================================================================
    // Registration through the real CodingAgent
    // ========================================================================

    private static AgentOptions BashAgentOptions() => new()
    {
        WorkDirectory = Environment.CurrentDirectory,
        EnableBash = true,
        EnableFileOps = false,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        AutoLoadWorkspaceInstructions = false,
    };

    private static async Task<AIFunction> GetRegisteredBashToolAsync()
    {
        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, BashAgentOptions());

        await agent.ExecuteAsync("hello", Ct);

        var tools = client.LastOptions?.Tools ?? new List<AITool>();
        return tools.OfType<AIFunction>().Single(f => f.Name == "execute_bash_command");
    }

    [Fact]
    public async Task RegisteredTool_ExposesOptionalTimeoutMs_AndNoCancellationToken()
    {
        var fn = await GetRegisteredBashToolAsync();

        var schema = fn.JsonSchema;
        Assert.True(schema.TryGetProperty("properties", out var properties));

        var names = properties.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("command", names);
        Assert.Contains("timeout_ms", names);
        Assert.DoesNotContain(names, n =>
            n.Equals("ct", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase));

        // command is required, timeout_ms is not.
        var required = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()).ToList()
            : new List<string?>();
        Assert.Contains("command", required);
        Assert.DoesNotContain("timeout_ms", required);

        // timeout_ms is an integer (nullable => integer or null).
        var timeoutSchema = properties.GetProperty("timeout_ms").GetRawText();
        Assert.Contains("integer", timeoutSchema);
    }

    [Fact]
    public async Task RegisteredTool_Description_DoesNotClaimPersistentShellSession()
    {
        var fn = await GetRegisteredBashToolAsync();

        Assert.DoesNotContain("persistent shell session", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fresh shell process", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeout_ms", fn.Description);
        Assert.Contains("900000", fn.Description);
    }

    [Fact]
    public async Task RegisteredTool_Description_DescribesTheOutputCapAndHowToReadAFullLog()
    {
        var fn = await GetRegisteredBashToolAsync();

        // One sentence that tells the model long output is shortened to head and tail and how to read
        // a full log instead: redirect to a file, then grep/tail/read_file.
        Assert.Contains("head and tail", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redirect", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grep", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read_file", fn.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisteredTool_CanBeInvoked_WithAndWithoutTimeout()
    {
        var fn = await GetRegisteredBashToolAsync();

        var omitted = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "echo registered-omitted" }),
            Ct);
        var explicitTimeout = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["command"] = "echo registered-explicit",
                ["timeout_ms"] = 5000
            }),
            Ct);

        Assert.Contains("registered-omitted", ResultToString(omitted));
        Assert.Contains("registered-explicit", ResultToString(explicitTimeout));
    }

    private static string ResultToString(object? result)
    {
        if (result is null) return string.Empty;
        if (result is string s) return s;
        if (result is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? (je.GetString() ?? string.Empty) : je.GetRawText();
        return result.ToString() ?? string.Empty;
    }
}
