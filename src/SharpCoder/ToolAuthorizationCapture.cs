using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCoder;

/// <summary>
/// The host tool-authorization pair captured at one point in time: the
/// <see cref="AgentOptions.AuthorizeTool"/> delegate and the
/// <see cref="AgentOptions.ToolAuthorizationCorrelationId"/> it authorizes with.
/// <para>
/// SharpCoder captures the pair ONCE — when a <see cref="CodingAgent"/> is constructed — and holds
/// that captured instance for the agent's whole lifetime. The pair is immutable: the correlation is
/// a <see cref="string"/> (an object that cannot be mutated in place) and the delegate reference can
/// never be re-pointed. Later changes to the options therefore affect only captures made afterwards,
/// never an existing agent.
/// </para>
/// </summary>
internal sealed class ToolAuthorizationCapture
{
    private ToolAuthorizationCapture(
        Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> authorizeTool,
        string? correlationId)
    {
        AuthorizeTool = authorizeTool;
        CorrelationId = correlationId;
    }

    /// <summary>The captured authorization callback. Never null — a disabled capture is <c>null</c> instead.</summary>
    internal Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> AuthorizeTool { get; }

    /// <summary>The captured host correlation, or <c>null</c>.</summary>
    internal string? CorrelationId { get; }

    /// <summary>
    /// Reads both authorization members from <paramref name="options"/> exactly once and freezes
    /// them. Returns <c>null</c> when no callback is configured, which is what keeps the
    /// no-callback tool surface byte-identical.
    /// <para>
    /// A <c>null</c> <paramref name="options"/> also yields <c>null</c> rather than throwing,
    /// deliberately. This method is called from the argument list of a <b>delegating constructor</b>
    /// (<see cref="SubAgents.SubAgentManager"/>'s public constructor), and C# evaluates a delegating
    /// argument list BEFORE the target constructor body runs. A null check here would therefore fire
    /// ahead of that constructor's own validation, reporting this method's parameter instead of the
    /// argument the caller actually got wrong and breaking the documented
    /// options → defaultClient → parentOptions order. Returning null instead leaves argument
    /// validation entirely to the constructor, which owns it.
    /// </para>
    /// <para>
    /// This is behavior-compatible by construction: <c>null</c> already means "authorization off"
    /// at every downstream consumer — <see cref="CodingAgent"/> registers its tools unguarded, and
    /// <see cref="SubAgents.SubAgentManager"/> builds its children with no callback — so there is no
    /// separate "absent" state this could be confused with.
    /// </para>
    /// </summary>
    /// <param name="options">The options to capture from; <c>null</c> yields a <c>null</c> capture.</param>
    internal static ToolAuthorizationCapture? Capture(AgentOptions? options)
    {
        if (options is null) return null;

        var authorizeTool = options.AuthorizeTool;
        if (authorizeTool is null) return null;

        return new ToolAuthorizationCapture(authorizeTool, options.ToolAuthorizationCorrelationId);
    }
}
