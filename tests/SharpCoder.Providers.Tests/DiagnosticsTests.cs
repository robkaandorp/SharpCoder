using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

using CopilotChoiceMergingHandler = SharpCoder.Providers.ChatClientFactory.CopilotChoiceMergingHandler;
using CopilotResponsesHandler = SharpCoder.Providers.ChatClientFactory.CopilotResponsesHandler;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Tests for the opt-in request/response diagnostics logging on <see cref="ChatClientFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is <b>behavioural</b>: each test drives a real HTTP exchange through the
/// production handler (<see cref="CopilotChoiceMergingHandler"/> or
/// <see cref="CopilotResponsesHandler"/>) over a fake terminal handler and then inspects the file
/// system. Asserting on <see cref="ChatClientFactory.ResolveDiagnosticsDirectory"/> alone would only
/// mirror the resolution logic; it would not prove that the two logging call sites actually consult
/// it, which is the property that matters.
/// </para>
/// <para>
/// The diagnostics state is process-wide static state and the environment variable is process-wide
/// too, so this class joins the serialized <c>EnvVarMutation</c> collection
/// (<c>DisableParallelization = true</c>, see <see cref="EnvVarMutationCollection"/>). The
/// constructor captures the original environment value and <see cref="Dispose"/> restores it,
/// clears the diagnostics cache, and removes every temporary directory the test created.
/// </para>
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class DiagnosticsTests : IDisposable
{
    private const string EnvVar = "SHARPCODER_DIAGNOSTICS_DIR";
    private const string CompletionsSubdirectory = "chat-completions";
    private const string ResponsesSubdirectory = "responses-api";

    /// <summary>
    /// The temp-path fallback the pre-opt-in implementation used when no directory was configured.
    /// Diagnostics must never write here again; the disabled-by-default tests assert that the
    /// contents of this directory are untouched, which fails if the fallback is reintroduced.
    /// </summary>
    private static readonly string LegacyFallbackDirectory =
        Path.Combine(Path.GetTempPath(), "copilothive-diagnostics");

    private readonly string? _originalEnvValue;
    private readonly Func<string?> _originalEnvironmentReader;
    private readonly Action? _originalCacheReadObserver;
    private readonly Action? _originalResetPublishObserver;
    private readonly List<string> _tempPaths = new();

    public DiagnosticsTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVar);
        _originalEnvironmentReader = ChatClientFactory.DiagnosticsEnvironmentReader;
        _originalCacheReadObserver = ChatClientFactory.DiagnosticsCacheReadObserver;
        _originalResetPublishObserver = ChatClientFactory.DiagnosticsResetPublishObserver;

        // Start every test from the documented default: no override, nothing cached, env cleared.
        Environment.SetEnvironmentVariable(EnvVar, null);
        ChatClientFactory.ResetDiagnosticsCache();
    }

    /// <summary>
    /// Restores the environment variable and all test seams, clears the process-wide diagnostics
    /// state, and deletes every temporary path this test created. All steps are attempted even if an
    /// earlier one fails, and the failures are aggregated so none is silently lost.
    /// </summary>
    public void Dispose()
    {
        var failures = new List<Exception>();

        // All seams are process-wide static state and the race tests replace them with delegates
        // that block, so they must be restored before anything else can resolve again.
        try
        {
            ChatClientFactory.DiagnosticsEnvironmentReader = _originalEnvironmentReader;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            ChatClientFactory.DiagnosticsCacheReadObserver = _originalCacheReadObserver;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            ChatClientFactory.DiagnosticsResetPublishObserver = _originalResetPublishObserver;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            Environment.SetEnvironmentVariable(EnvVar, _originalEnvValue);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            ChatClientFactory.ResetDiagnosticsCache();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Diagnostics test cleanup failed.", failures);
    }

    // ── Disabled by default ──────────────────────────────────────────────────

    #region Disabled by default — no directory configured means nothing is written

    /// <summary>
    /// With neither the environment variable nor an explicit override set, a completions exchange
    /// must write nothing at all — in particular not to the temp-path fallback the previous
    /// implementation defaulted to.
    /// </summary>
    [Fact]
    public async Task Disabled_ByDefault_CompletionsExchange_WritesNothing()
    {
        Assert.Null(ChatClientFactory.ResolveDiagnosticsDirectory());

        var marker = NewMarker();
        await DriveCompletionsExchangeAsync(requestBody: MarkerRequestBody(marker));

        // The old temp-path fallback is gone: this exchange's payload appears nowhere in it.
        AssertMarkerAbsentFromDefaultRoots(marker);
    }

    /// <summary>
    /// The same default applies to the responses-API handler, whose logging is a separate call site.
    /// </summary>
    [Fact]
    public async Task Disabled_ByDefault_ResponsesExchange_WritesNothing()
    {
        var marker = NewMarker();
        await DriveResponsesExchangeAsync(requestBody: MarkerRequestBody(marker));

        AssertMarkerAbsentFromDefaultRoots(marker);
    }

    /// <summary>
    /// Once diagnostics are switched off again, the previously configured directory must stay
    /// untouched — proving the guard short-circuits before any file-system access, rather than
    /// creating the directory and writing nothing into it.
    /// </summary>
    [Fact]
    public async Task Disabled_AfterBeingEnabled_CreatesNoDirectoryAndWritesNothing()
    {
        var dir = ReserveTempPath();

        ChatClientFactory.SetDiagnosticsDirectory(null);

        await DriveCompletionsExchangeAsync();

        // Not merely empty — never created.
        Assert.False(Directory.Exists(dir));
    }

    #endregion

    // ── Enabled through the environment variable ─────────────────────────────

    #region SHARPCODER_DIAGNOSTICS_DIR — non-empty enables, empty disables

    /// <summary>
    /// A non-empty <c>SHARPCODER_DIAGNOSTICS_DIR</c> enables logging: the completions exchange must
    /// produce the request and response files, carrying the actual bodies.
    /// </summary>
    [Fact]
    public async Task EnvVarSet_CompletionsExchange_WritesRequestAndResponseFiles()
    {
        var dir = ReserveTempPath();
        SetEnvironmentDirectory(dir);

        Assert.Equal(dir, ChatClientFactory.ResolveDiagnosticsDirectory());

        await DriveCompletionsExchangeAsync(
            requestBody: RequestBody, responseBody: CompletionsResponseBody);

        var written = SnapshotDirectory(Path.Combine(dir, CompletionsSubdirectory));
        Assert.Equal(new[] { "0001_request.json", "0001_response.json" }, written);

        Assert.Equal(RequestBody, ReadDiagnostic(dir, CompletionsSubdirectory, "0001_request.json"));
        Assert.Equal(
            CompletionsResponseBody, ReadDiagnostic(dir, CompletionsSubdirectory, "0001_response.json"));
    }

    /// <summary>
    /// The responses-API handler honours the same environment variable, writing into its own
    /// <c>responses-api</c> subdirectory.
    /// </summary>
    [Fact]
    public async Task EnvVarSet_ResponsesExchange_WritesIntoResponsesSubdirectory()
    {
        var dir = ReserveTempPath();
        SetEnvironmentDirectory(dir);

        await DriveResponsesExchangeAsync(responseBody: ResponsesResponseBody);

        var written = SnapshotDirectory(Path.Combine(dir, ResponsesSubdirectory));
        Assert.Equal(new[] { "0001_request.json", "0001_response.json" }, written);

        Assert.Equal(
            ResponsesResponseBody, ReadDiagnostic(dir, ResponsesSubdirectory, "0001_response.json"));

        // The completions subdirectory belongs to the other handler and must not be created here.
        Assert.False(Directory.Exists(Path.Combine(dir, CompletionsSubdirectory)));
    }

    /// <summary>
    /// An empty or whitespace-only environment value means "unset", so diagnostics stay disabled.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnvVarEmptyOrWhitespace_KeepsDiagnosticsDisabled(string value)
    {
        Environment.SetEnvironmentVariable(EnvVar, value);
        ChatClientFactory.ResetDiagnosticsCache();

        Assert.Null(ChatClientFactory.ResolveDiagnosticsDirectory());

        var marker = NewMarker();
        await DriveCompletionsExchangeAsync(requestBody: MarkerRequestBody(marker));

        AssertMarkerAbsentFromDefaultRoots(marker);
    }

    #endregion

    // ── Enabled through the explicit API ─────────────────────────────────────

    #region SetDiagnosticsDirectory — enables, and overrides the environment variable

    /// <summary>
    /// <see cref="ChatClientFactory.SetDiagnosticsDirectory"/> with a real path enables logging even
    /// when the environment variable is not set at all.
    /// </summary>
    [Fact]
    public async Task SetDiagnosticsDirectory_WithPath_WritesFiles()
    {
        var dir = ReserveTempPath();

        ChatClientFactory.SetDiagnosticsDirectory(dir);

        await DriveCompletionsExchangeAsync(
            requestBody: RequestBody, responseBody: CompletionsResponseBody);

        Assert.Equal(
            new[] { "0001_request.json", "0001_response.json" },
            SnapshotDirectory(Path.Combine(dir, CompletionsSubdirectory)));
        Assert.Equal(RequestBody, ReadDiagnostic(dir, CompletionsSubdirectory, "0001_request.json"));
    }

    /// <summary>
    /// The explicit override wins over the environment variable: files land in the override
    /// directory and none in the one the environment variable names.
    /// </summary>
    [Fact]
    public async Task SetDiagnosticsDirectory_OverridesEnvVarDirectory()
    {
        var envDir = ReserveTempPath();
        var overrideDir = ReserveTempPath();
        SetEnvironmentDirectory(envDir);

        ChatClientFactory.SetDiagnosticsDirectory(overrideDir);

        await DriveCompletionsExchangeAsync();

        Assert.NotEmpty(SnapshotDirectory(Path.Combine(overrideDir, CompletionsSubdirectory)));
        Assert.False(Directory.Exists(envDir));
    }

    /// <summary>
    /// <c>SetDiagnosticsDirectory(null)</c> disables logging <b>even while the environment variable
    /// is set</b>. This is the property the empty-string "explicitly disabled" sentinel exists for:
    /// storing a plain <see langword="null"/> would be indistinguishable from "no override" and
    /// would let the environment variable re-enable logging.
    /// </summary>
    [Fact]
    public async Task SetDiagnosticsDirectoryNull_DisablesEvenWhenEnvVarIsSet()
    {
        var envDir = ReserveTempPath();
        SetEnvironmentDirectory(envDir);

        // Precondition: the environment variable alone really would have enabled logging.
        Assert.Equal(envDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        ChatClientFactory.SetDiagnosticsDirectory(null);

        Assert.Null(ChatClientFactory.ResolveDiagnosticsDirectory());

        await DriveCompletionsExchangeAsync();

        Assert.False(Directory.Exists(envDir));
    }

    /// <summary>
    /// An empty or whitespace-only path is treated exactly like <see langword="null"/>: diagnostics
    /// are disabled, and the still-set environment variable does not resurrect them.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task SetDiagnosticsDirectory_EmptyOrWhitespace_DisablesEvenWhenEnvVarIsSet(string path)
    {
        var envDir = ReserveTempPath();
        SetEnvironmentDirectory(envDir);

        ChatClientFactory.SetDiagnosticsDirectory(path);

        Assert.Null(ChatClientFactory.ResolveDiagnosticsDirectory());

        await DriveCompletionsExchangeAsync();

        Assert.False(Directory.Exists(envDir));
    }

    #endregion

    // ── Cache reset ──────────────────────────────────────────────────────────

    #region ResetDiagnosticsCache — clears the override and re-reads the environment variable

    /// <summary>
    /// After the environment value has been cached, changing it has no effect until
    /// <see cref="ChatClientFactory.ResetDiagnosticsCache"/> is called — and afterwards the new
    /// value is picked up. Both halves are asserted, so the test fails if the read is not cached
    /// <em>and</em> if the reset does not re-read.
    /// </summary>
    [Fact]
    public async Task ResetDiagnosticsCache_ReReadsEnvironmentVariable()
    {
        var firstDir = ReserveTempPath();
        var secondDir = ReserveTempPath();

        SetEnvironmentDirectory(firstDir);
        Assert.Equal(firstDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        // Cached: the change is not observed yet.
        Environment.SetEnvironmentVariable(EnvVar, secondDir);
        Assert.Equal(firstDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        ChatClientFactory.ResetDiagnosticsCache();

        Assert.Equal(secondDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        await DriveCompletionsExchangeAsync();

        Assert.NotEmpty(SnapshotDirectory(Path.Combine(secondDir, CompletionsSubdirectory)));
        Assert.False(Directory.Exists(firstDir));
    }

    /// <summary>
    /// <see cref="ChatClientFactory.ResetDiagnosticsCache"/> also drops the explicit override, so
    /// resolution falls back to the environment variable again.
    /// </summary>
    [Fact]
    public async Task ResetDiagnosticsCache_ClearsOverride_FallingBackToEnvironmentVariable()
    {
        var envDir = ReserveTempPath();
        var overrideDir = ReserveTempPath();
        SetEnvironmentDirectory(envDir);

        ChatClientFactory.SetDiagnosticsDirectory(overrideDir);
        Assert.Equal(overrideDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        ChatClientFactory.ResetDiagnosticsCache();

        Assert.Equal(envDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        await DriveCompletionsExchangeAsync();

        Assert.NotEmpty(SnapshotDirectory(Path.Combine(envDir, CompletionsSubdirectory)));
        Assert.False(Directory.Exists(overrideDir));
    }

    /// <summary>
    /// A reset with no environment variable set returns to the disabled default.
    /// </summary>
    [Fact]
    public void ResetDiagnosticsCache_WithoutEnvironmentVariable_RestoresDisabledDefault()
    {
        ChatClientFactory.SetDiagnosticsDirectory(ReserveTempPath());
        Assert.NotNull(ChatClientFactory.ResolveDiagnosticsDirectory());

        ChatClientFactory.ResetDiagnosticsCache();

        Assert.Null(ChatClientFactory.ResolveDiagnosticsDirectory());
    }

    #endregion

    // ── Reset/resolve race ───────────────────────────────────────────────────

    #region ResetDiagnosticsCache is atomic with respect to an in-flight resolve

    /// <summary>
    /// A resolve that sampled the environment <em>before</em> a reset must not publish that stale
    /// value <em>after</em> the reset — and every later resolution must observe the post-reset
    /// environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the regression test for the reset/resolve race. The interleaving is forced
    /// deterministically through the <see cref="ChatClientFactory.DiagnosticsEnvironmentReader"/>
    /// seam rather than by timing jitter: the background resolve is suspended at exactly the point
    /// between reading the environment variable and committing the cache, the reset runs to
    /// completion while it is parked there, and only then is it released.
    /// </para>
    /// <para>
    /// Without the epoch guard the released resolve republishes the stale directory and sets the
    /// "already read" flag, so the final assertions observe the pre-reset path forever.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ResolveInFlightDuringReset_CannotCommitStaleState()
    {
        var staleDir = ReserveTempPath();
        var freshDir = ReserveTempPath();

        using var readerEntered = new ManualResetEventSlim(false);
        using var releaseReader = new ManualResetEventSlim(false);

        SetEnvironmentDirectory(staleDir);

        // First read: park inside the reader, having already observed the PRE-reset value.
        // Later reads (the retry, and anything after) must see the post-reset environment.
        var firstRead = 1;
        var released = new StrongBox<bool>(false);
        ChatClientFactory.DiagnosticsEnvironmentReader = () =>
        {
            var value = Environment.GetEnvironmentVariable(EnvVar);

            if (Interlocked.Exchange(ref firstRead, 0) == 1)
            {
                readerEntered.Set();
                // Wait for the reset to complete before this stale read is allowed to commit.
                // A timed-out gate is recorded and asserted on, so it cannot pass silently.
                WaitAtGate(releaseReader, released);
            }

            return value;
        };

        var resolveTask = Task.Run(ChatClientFactory.ResolveDiagnosticsDirectory, TestContext.Current.CancellationToken);

        try
        {
            // Deterministic rendezvous: the resolve is now holding the stale value, uncommitted.
            Assert.True(
                readerEntered.Wait(GateTimeout, TestContext.Current.CancellationToken),
                "The resolve never reached the environment read.");

            // Swap the environment and reset while the stale resolve is parked.
            Environment.SetEnvironmentVariable(EnvVar, freshDir);
            ChatClientFactory.ResetDiagnosticsCache();
        }
        finally
        {
            releaseReader.Set();
        }

        var resolved = await resolveTask.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        AssertGateSignalled(released, "The resolver's release gate");

        // The in-flight resolve must not have returned the stale directory: on detecting the reset
        // it retries and observes the new environment value.
        Assert.Equal(freshDir, resolved);

        // Requirement 1: after the reset returned, resolution reflects the post-reset environment.
        Assert.Equal(freshDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        // Requirement 2: the stale value was never committed to the cache, so writes go to the new
        // directory and not to the pre-reset one.
        await DriveCompletionsExchangeAsync();

        Assert.NotEmpty(SnapshotDirectory(Path.Combine(freshDir, CompletionsSubdirectory)));
        Assert.False(Directory.Exists(staleDir));
    }

    /// <summary>
    /// A reset must not be clobbered by an in-flight resolve even when the environment ends up
    /// <em>disabled</em>: the parked resolve holds a real directory, the reset happens after the
    /// variable is cleared, and the outcome must be "disabled" rather than the stale path.
    /// </summary>
    [Fact]
    public async Task ResolveInFlightDuringReset_ToClearedEnvironment_EndsDisabled()
    {
        var staleDir = ReserveTempPath();

        using var readerEntered = new ManualResetEventSlim(false);
        using var releaseReader = new ManualResetEventSlim(false);

        SetEnvironmentDirectory(staleDir);

        var firstRead = 1;
        var released = new StrongBox<bool>(false);
        ChatClientFactory.DiagnosticsEnvironmentReader = () =>
        {
            var value = Environment.GetEnvironmentVariable(EnvVar);

            if (Interlocked.Exchange(ref firstRead, 0) == 1)
            {
                readerEntered.Set();
                WaitAtGate(releaseReader, released);
            }

            return value;
        };

        var resolveTask = Task.Run(ChatClientFactory.ResolveDiagnosticsDirectory, TestContext.Current.CancellationToken);

        try
        {
            Assert.True(
                readerEntered.Wait(GateTimeout, TestContext.Current.CancellationToken),
                "The resolve never reached the environment read.");

            Environment.SetEnvironmentVariable(EnvVar, null);
            ChatClientFactory.ResetDiagnosticsCache();
        }
        finally
        {
            releaseReader.Set();
        }

        var resolved = await resolveTask.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        AssertGateSignalled(released, "The resolver's release gate");

        Assert.Null(resolved);
        Assert.Null(ChatClientFactory.ResolveDiagnosticsDirectory());

        // Diagnostics really are off: the exchange writes nothing anywhere.
        var marker = NewMarker();
        await DriveCompletionsExchangeAsync(requestBody: MarkerRequestBody(marker));

        Assert.False(Directory.Exists(staleDir));
        AssertMarkerAbsentFromDefaultRoots(marker);
    }

    /// <summary>
    /// A resolver parked on the <em>cached fast path</em> — it has already sampled the cache state
    /// but not yet acted on it — must still return a self-consistent answer when a reset completes
    /// while it is parked. It may report the pre-reset value (its snapshot) or the post-reset value,
    /// but never <see langword="null"/>, which corresponds to neither state when the environment
    /// variable is non-empty on both sides of the reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the regression test for the fast-path atomicity defect. When the read flag and the
    /// cached value lived in two separate fields, this exact interleaving tore them apart: the
    /// resolver read the flag as <c>true</c>, the reset then cleared the value and lowered the flag,
    /// and the resolver went on to read the freshly-cleared <see langword="null"/> and return it.
    /// </para>
    /// <para>
    /// The interleaving is forced deterministically through the
    /// <see cref="ChatClientFactory.DiagnosticsCacheReadObserver"/> seam, which fires immediately
    /// after the resolver samples the cache — precisely where the tear used to occur — rather than
    /// relying on timing jitter.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ResolverParkedOnCachedFastPath_DuringReset_NeverReturnsTornNull()
    {
        const string cachedDir = "/cached-dir";
        const string freshDir = "/fresh-dir";

        using var observerEntered = new ManualResetEventSlim(false);
        using var releaseObserver = new ManualResetEventSlim(false);

        // Prime the cache so the next resolve takes the cached fast path.
        SetEnvironmentDirectory(cachedDir);
        Assert.Equal(cachedDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        var firstObservation = 1;
        var released = new StrongBox<bool>(false);
        ChatClientFactory.DiagnosticsCacheReadObserver = () =>
        {
            if (Interlocked.Exchange(ref firstObservation, 0) != 1) return;

            observerEntered.Set();
            // Park here — holding an already-sampled cache state — until the reset has completed.
            // Whether the gate was really signalled is asserted below, so a timeout cannot pass
            // silently as a resolver that simply ran before the reset.
            WaitAtGate(releaseObserver, released);
        };

        var resolveTask = Task.Run(ChatClientFactory.ResolveDiagnosticsDirectory, TestContext.Current.CancellationToken);

        string? resolved;
        try
        {
            Assert.True(
                observerEntered.Wait(GateTimeout, TestContext.Current.CancellationToken),
                "The resolve never reached the cached fast path.");

            // Reset (and re-point the environment) while the resolver is parked mid-fast-path.
            Environment.SetEnvironmentVariable(EnvVar, freshDir);
            ChatClientFactory.ResetDiagnosticsCache();
        }
        finally
        {
            // Always release, so a failed assertion above cannot leave the resolver parked for the
            // full timeout (or deadlock the awaits below).
            releaseObserver.Set();
        }

        resolved = await resolveTask.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // The parked resolver must have resumed because the gate opened, not because it timed out —
        // otherwise it ran before the reset and the value below proves nothing.
        AssertGateSignalled(released, "The resolver's release gate");

        // THE ASSERTION THAT MATTERS: the resolver's ACTUAL returned value must be one of the two
        // legitimate states. A torn read yields null, which is neither.
        Assert.NotNull(resolved);
        Assert.True(
            resolved == cachedDir || resolved == freshDir,
            $"Resolver returned '{resolved}', which matches neither the pre-reset ('{cachedDir}') " +
            $"nor the post-reset ('{freshDir}') state — the cache read was torn.");

        // And the post-reset state is what every later resolution reports.
        Assert.Equal(freshDir, ChatClientFactory.ResolveDiagnosticsDirectory());
    }

    /// <summary>
    /// The same fast-path interleaving, repeated across many resolvers whose results are all
    /// inspected, so a torn read is caught no matter which resolver observes it.
    /// </summary>
    /// <remarks>
    /// Unlike a start-gate stress loop, every iteration here is deterministic: the parked resolver
    /// is guaranteed to sit between sampling the cache and acting on it while the reset runs, and
    /// <b>every</b> resolver's returned value is asserted rather than discarded.
    /// </remarks>
    [Fact]
    public async Task ResolversParkedOnCachedFastPath_DuringReset_AlwaysReturnValidState()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var cachedDir = $"/cached-{iteration}";
            var freshDir = $"/fresh-{iteration}";

            using var observerEntered = new ManualResetEventSlim(false);
            using var releaseObserver = new ManualResetEventSlim(false);

            ChatClientFactory.DiagnosticsCacheReadObserver = null;
            SetEnvironmentDirectory(cachedDir);
            Assert.Equal(cachedDir, ChatClientFactory.ResolveDiagnosticsDirectory());

            var firstObservation = 1;
            var released = new StrongBox<bool>(false);
            ChatClientFactory.DiagnosticsCacheReadObserver = () =>
            {
                if (Interlocked.Exchange(ref firstObservation, 0) != 1) return;

                observerEntered.Set();
                WaitAtGate(releaseObserver, released);
            };

            var resolveTask = Task.Run(
                ChatClientFactory.ResolveDiagnosticsDirectory, TestContext.Current.CancellationToken);

            try
            {
                Assert.True(
                    observerEntered.Wait(GateTimeout, TestContext.Current.CancellationToken),
                    "The resolve never reached the cached fast path.");

                Environment.SetEnvironmentVariable(EnvVar, freshDir);
                ChatClientFactory.ResetDiagnosticsCache();
            }
            finally
            {
                releaseObserver.Set();
            }

            var resolved = await resolveTask.WaitAsync(
                GateTimeout, TestContext.Current.CancellationToken);

            AssertGateSignalled(released, $"Iteration {iteration}: the resolver's release gate");

            Assert.True(
                resolved == cachedDir || resolved == freshDir,
                $"Iteration {iteration}: resolver returned '{resolved ?? "<null>"}', which matches " +
                $"neither the pre-reset ('{cachedDir}') nor the post-reset ('{freshDir}') state.");
        }
    }

    /// <summary>
    /// A resolver running while a reset is midway through its transition must observe either the
    /// whole pre-reset state or the whole post-reset state — never the cleared override paired with
    /// the still-cached environment value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the regression test for the override/cache tear. The scenario is the one from the
    /// review: the environment value <c>C</c> is cached, an explicit override <c>O</c> is set, the
    /// environment is then re-pointed to <c>N</c>, and a reset begins. When the override and the
    /// cache were two separate publications, a resolver could see the already-cleared override
    /// together with the not-yet-cleared cache and return <c>C</c> — neither the pre-reset answer
    /// (<c>O</c>) nor the post-reset one (<c>N</c>).
    /// </para>
    /// <para>
    /// The interleaving is forced deterministically through the
    /// <see cref="ChatClientFactory.DiagnosticsResetPublishObserver"/> seam, which fires inside the
    /// reset at exactly the point between the two former writes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ResolverRunningDuringResetTransition_NeverSeesClearedOverrideWithStaleCache()
    {
        const string cachedDir = "/cached-env";
        const string overrideDir = "/explicit-override";
        const string freshDir = "/fresh-env";

        using var resetReachedMidpoint = new ManualResetEventSlim(false);
        using var releaseReset = new ManualResetEventSlim(false);

        // Cache C, then set override O — exactly the state described in the review.
        SetEnvironmentDirectory(cachedDir);
        Assert.Equal(cachedDir, ChatClientFactory.ResolveDiagnosticsDirectory());
        ChatClientFactory.SetDiagnosticsDirectory(overrideDir);
        Assert.Equal(overrideDir, ChatClientFactory.ResolveDiagnosticsDirectory());

        // Re-point the environment to N, so the post-reset answer differs from the cached value.
        Environment.SetEnvironmentVariable(EnvVar, freshDir);

        var firstObservation = 1;
        var released = new StrongBox<bool>(false);
        ChatClientFactory.DiagnosticsResetPublishObserver = () =>
        {
            if (Interlocked.Exchange(ref firstObservation, 0) != 1) return;

            resetReachedMidpoint.Set();
            WaitAtGate(releaseReset, released);
        };

        var resetTask = Task.Run(ChatClientFactory.ResetDiagnosticsCache, TestContext.Current.CancellationToken);

        string? resolved;
        try
        {
            Assert.True(
                resetReachedMidpoint.Wait(GateTimeout, TestContext.Current.CancellationToken),
                "The reset never reached its publication midpoint.");

            // Resolve while the reset is suspended mid-transition.
            resolved = ChatClientFactory.ResolveDiagnosticsDirectory();
        }
        finally
        {
            releaseReset.Set();
        }

        await resetTask.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        AssertGateSignalled(released, "The reset's release gate");

        // THE ASSERTION THAT MATTERS: the mid-reset resolve must report a whole state. Returning the
        // cached environment value means it saw the cleared override with the stale cache.
        Assert.True(
            resolved == overrideDir || resolved == freshDir,
            $"Resolver returned '{resolved ?? "<null>"}', which matches neither the pre-reset " +
            $"('{overrideDir}') nor the post-reset ('{freshDir}') state — the reset was observed " +
            "half-applied (cleared override paired with the stale cached environment value).");

        // After the reset completes, resolution reflects the new environment.
        Assert.Equal(freshDir, ChatClientFactory.ResolveDiagnosticsDirectory());
    }

    #endregion

    // ── I/O failures are swallowed ───────────────────────────────────────────

    #region Best-effort logging — a write failure never breaks the exchange

    /// <summary>
    /// When the configured diagnostics directory is unusable — here a regular <em>file</em> stands
    /// where the directory should be, so <see cref="Directory.CreateDirectory(string)"/> fails — the
    /// resulting I/O exception must be swallowed and the exchange must complete normally with its
    /// response body intact.
    /// </summary>
    [Fact]
    public async Task WriteFailure_IsSwallowed_AndExchangeStillSucceeds()
    {
        var blockingFile = ReserveTempPath();
        File.WriteAllText(blockingFile, "not a directory");

        ChatClientFactory.SetDiagnosticsDirectory(blockingFile);

        // Sanity: writing under this path really is impossible. The exact exception type is
        // platform-dependent (DirectoryNotFoundException on Unix, IOException on Windows), so only
        // the common base type is asserted.
        Assert.ThrowsAny<IOException>(
            () => Directory.CreateDirectory(Path.Combine(blockingFile, CompletionsSubdirectory)));

        var body = await DriveCompletionsExchangeAsync(
            requestBody: RequestBody, responseBody: CompletionsResponseBody);

        // The exchange completed and its payload is untouched by the failed logging attempt.
        Assert.Contains("hello", body);
        Assert.True(File.Exists(blockingFile));
    }

    /// <summary>
    /// The same guarantee holds for the responses-API logging call site.
    /// </summary>
    [Fact]
    public async Task WriteFailure_OnResponsesPath_IsSwallowed_AndExchangeStillSucceeds()
    {
        var blockingFile = ReserveTempPath();
        File.WriteAllText(blockingFile, "not a directory");

        ChatClientFactory.SetDiagnosticsDirectory(blockingFile);

        var body = await DriveResponsesExchangeAsync(responseBody: ResponsesResponseBody);

        Assert.Contains("output", body);
    }

    #endregion

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string RequestBody = """{"model":"claude-sonnet-4.6","messages":[]}""";

    private const string CompletionsResponseBody =
        """{"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"model":"test"}""";

    private const string ResponsesResponseBody =
        """{"output":[{"type":"message","content":[{"type":"output_text","text":"hello"}]}]}""";

    /// <summary>
    /// Sets the environment variable and clears the cache so the new value is actually observed.
    /// </summary>
    private static void SetEnvironmentDirectory(string dir)
    {
        Environment.SetEnvironmentVariable(EnvVar, dir);
        ChatClientFactory.ResetDiagnosticsCache();
    }

    /// <summary>
    /// Reserves a unique path under the system temp directory and registers it for deletion. The
    /// path is deliberately <b>not</b> created, so tests can assert that diagnostics never brought
    /// it into existence.
    /// </summary>
    private string ReserveTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpcoder-diagnostics-tests-{Guid.NewGuid():N}");
        _tempPaths.Add(path);
        return path;
    }

    /// <summary>Returns the sorted file names in a directory, or an empty array if it is absent.</summary>
    private static string[] SnapshotDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        var names = Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return names!;
    }

    /// <summary>
    /// Asserts that this exchange's payload was not written under any directory a disabled
    /// implementation might plausibly fall back to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot of file <em>names</em> would not do: the fallback directories are shared
    /// process-wide and the handlers restart their sequence numbering per instance, so a
    /// reintroduced fallback would simply overwrite an existing <c>0001_request.json</c> and leave
    /// the name set unchanged. Searching for a per-test unique marker inside the file contents
    /// detects the write regardless of whether it created or overwrote a file.
    /// </para>
    /// <para>
    /// Both the historical <c>copilothive-diagnostics</c> fallback and the bare temp path are
    /// checked, since a regressed guard could route either way.
    /// </para>
    /// </remarks>
    private static void AssertMarkerAbsentFromDefaultRoots(string marker)
    {
        foreach (var root in new[] { LegacyFallbackDirectory, Path.GetTempPath() })
        {
            foreach (var subdirectory in new[] { CompletionsSubdirectory, ResponsesSubdirectory })
                AssertMarkerAbsentFrom(Path.Combine(root, subdirectory), marker);
        }

        // The legacy root also held loose files directly, not only the two subdirectories.
        AssertMarkerAbsentFrom(LegacyFallbackDirectory, marker);
    }

    /// <summary>Asserts that no file directly inside <paramref name="directory"/> contains the marker.</summary>
    private static void AssertMarkerAbsentFrom(string directory, string marker)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var path in Directory.GetFiles(directory))
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                // A file being written by an unrelated process cannot be this exchange's output.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            Assert.DoesNotContain(marker, content, StringComparison.Ordinal);
        }
    }

    /// <summary>A value unique to one test run, used to identify a specific exchange's payload.</summary>
    private static string NewMarker() => $"sharpcoder-diagnostics-marker-{Guid.NewGuid():N}";

    /// <summary>
    /// The longest a test will wait at a rendezvous point before declaring the interleaving broken.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Blocks a parked background thread on <paramref name="gate"/> and <b>records</b> whether it
    /// was actually signalled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ignoring the <see cref="ManualResetEventSlim.Wait(TimeSpan)"/> return value would let these
    /// tests pass vacuously: if the controlling thread were delayed past the timeout, the parked
    /// resolver would silently resume <em>before</em> the reset, return the pre-reset value, and the
    /// test would then accept that value as legitimate — without ever having exercised the overlap
    /// it claims to test. The flag returned here is asserted on the controlling thread, so a
    /// timed-out gate fails the test loudly instead.
    /// </para>
    /// <para>
    /// The result is written to a holder rather than thrown, because an exception raised inside a
    /// production callback would be observed as a resolve failure rather than as the gate problem
    /// it actually is.
    /// </para>
    /// </remarks>
    private static void WaitAtGate(ManualResetEventSlim gate, StrongBox<bool> signalled) =>
        signalled.Value = gate.Wait(GateTimeout);

    /// <summary>
    /// Asserts that a parked thread's gate was signalled rather than abandoned on a timeout.
    /// </summary>
    private static void AssertGateSignalled(StrongBox<bool> signalled, string what) =>
        Assert.True(
            signalled.Value,
            $"{what} was never signalled within {GateTimeout.TotalSeconds:F0}s — the parked thread " +
            "resumed on a timeout, so the intended interleaving did not happen and the result " +
            "proves nothing.");

    private static string MarkerRequestBody(string marker) =>
        $$"""{"model":"claude-sonnet-4.6","marker":"{{marker}}","messages":[]}""";

    private static string ReadDiagnostic(string root, string subdirectory, string fileName) =>
        File.ReadAllText(Path.Combine(root, subdirectory, fileName));

    /// <summary>
    /// Drives one full completions exchange through the production
    /// <see cref="CopilotChoiceMergingHandler"/> over a fake terminal handler, and returns the
    /// response body the caller would observe.
    /// </summary>
    private static async Task<string> DriveCompletionsExchangeAsync(
        string requestBody = RequestBody, string responseBody = CompletionsResponseBody)
    {
        using var client = new HttpClient(
            new CopilotChoiceMergingHandler(new FakeTerminalHandler(responseBody, "application/json")));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Drives one full responses-API exchange through the production
    /// <see cref="CopilotResponsesHandler"/> over a fake terminal handler, and returns the response
    /// body the caller would observe.
    /// </summary>
    private static async Task<string> DriveResponsesExchangeAsync(
        string requestBody = "{}", string responseBody = ResponsesResponseBody)
    {
        using var client = new HttpClient(
            new CopilotResponsesHandler(new FakeTerminalHandler(responseBody, "application/json")));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Terminal handler that returns a canned successful response.</summary>
    private sealed class FakeTerminalHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            });
    }
}
