using Microsoft.Extensions.AI;
using SharpCoder;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>
/// Regression tests for the public <see cref="SubAgentManager"/> constructor's argument
/// validation.
/// <para>
/// The round-2 change routed the public constructor through a delegating constructor:
/// <c>public SubAgentManager(options, client, parentOptions, logger)</c> now forwards to the
/// internal overload that takes an explicit authorization capture. The delegating argument list
/// evaluates <c>ToolAuthorizationCapture.Capture(parentOptions)</c> BEFORE the target constructor
/// body runs, and that capture dereferences <c>parentOptions</c> — so a null
/// <c>parentOptions</c> now surfaces as an <see cref="ArgumentNullException"/> for
/// <c>"options"</c> instead of <c>"parentOptions"</c>. The exception type is unchanged, but the
/// <see cref="ArgumentException.ParamName"/> a host sees is different, and the message names the
/// wrong argument.
/// </para>
/// <para>
/// At the merge base (58a85af) all four cases below pass; against the round-2 code two of them
/// fail. They are written here as the specification of the CORRECT behaviour, so the defect is
/// visible rather than hidden.
/// </para>
/// </summary>
public class SubAgentManagerConstructorValidationTests
{
    private static AgentOptions ParentOptions() => new() { WorkDirectory = Path.GetTempPath() };

    [Fact]
    public void Constructor_NullDefaultClient_ReportsDefaultClient()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(new SubAgentOptions(), null!, ParentOptions()));

        Assert.Equal("defaultClient", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullParentOptions_ReportsParentOptions()
    {
        // REGRESSION (round 2): the delegating constructor evaluates the authorization capture
        // before the target body validates parentOptions, so the capture's own null check wins
        // and the host is told "options" instead of "parentOptions".
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(new SubAgentOptions(), new CapturingClient(), null!));

        Assert.Equal("parentOptions", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDefaultClientAndNullParentOptions_ReportsDefaultClientFirst()
    {
        // The validation order is part of the contract: the default client is checked before the
        // parent options, so a host that passes both null is told about the FIRST missing one.
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(new SubAgentOptions(), null!, null!));

        Assert.Equal("defaultClient", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullOptions_ReportsOptions()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(null!, new CapturingClient(), ParentOptions()));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullOptionsAndNullDefaultClient_ReportsOptionsFirst()
    {
        // The OTHER half of the validation order: the OPTIONS are checked before the default
        // client. Together with
        // Constructor_NullDefaultClientAndNullParentOptions_ReportsDefaultClientFirst this pins
        // the whole documented order options -> defaultClient -> parentOptions.
        //
        // Before this test existed, swapping the 'options' and 'defaultClient' checks in the
        // constructor was invisible to the ENTIRE suite (verified: the mutant passes 848/848).
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(null!, null!, ParentOptions()));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullOptionsAndNullParentOptions_ReportsOptionsFirst()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(null!, new CapturingClient(), null!));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Constructor_AllArgumentsNull_ReportsOptionsFirst()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SubAgentManager(null!, null!, null!));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Capture_NullOptions_MeansAuthorizationOff_AndNeverThrows()
    {
        // The repair made ToolAuthorizationCapture.Capture null-safe: a null AgentOptions yields
        // a null capture instead of throwing. The null capture must still mean "authorization
        // off" everywhere, never "authorization on" and never an exception.
        var capture = ToolAuthorizationCapture.Capture(null!);

        Assert.Null(capture);
    }

    /// <summary>A client that never has to answer anything.</summary>
    private sealed class CapturingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                FinishReason = ChatFinishReason.Stop
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
