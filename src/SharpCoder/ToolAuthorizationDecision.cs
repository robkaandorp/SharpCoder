using System;

namespace SharpCoder;

/// <summary>
/// The answer to one <see cref="AgentOptions.AuthorizeTool"/> check: allow the tool call to run, or
/// deny it with a reason the model may see.
/// <para>
/// A decision is immutable and is created only through <see cref="Allow"/> or
/// <see cref="Deny(string)"/>.
/// </para>
/// <para>
/// <b>Fail-closed contract:</b> SharpCoder only executes a guarded tool call when the callback
/// returns <see cref="Allow"/>. A <c>null</c> decision (a callback that returned no decision) or a
/// denial whose reason is <c>null</c>/blank is refused with a fixed, host-neutral message, and a
/// callback fault is refused with a different fixed message. The model never sees anything but
/// those fixed messages or the host-authored reason.
/// </para>
/// </summary>
public sealed class ToolAuthorizationDecision
{
    private ToolAuthorizationDecision(bool isAllowed, string? reason)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    /// <summary>
    /// The shared decision that authorizes the tool call. The wrapped function then runs exactly as
    /// it would have without the authorization callback.
    /// </summary>
    public static ToolAuthorizationDecision Allow { get; } = new(isAllowed: true, reason: null);

    /// <summary>
    /// Creates a decision that refuses the tool call. The call is <b>not</b> executed, and the model
    /// receives a refusal that includes <paramref name="reason"/>.
    /// <para>
    /// <paramref name="reason"/> is host-authored text that becomes visible to the model: write it
    /// for the model to read (for example <c>"The current user may not write files."</c>) and never
    /// put secrets, stack traces, or internal host state in it. When it is <c>null</c> or blank the
    /// model receives a fixed, host-neutral refusal instead.
    /// </para>
    /// </summary>
    /// <param name="reason">Host-approved, model-visible explanation of the denial.</param>
    /// <returns>A denial decision carrying <paramref name="reason"/>.</returns>
    public static ToolAuthorizationDecision Deny(string? reason) => new(isAllowed: false, reason: reason);

    /// <summary>
    /// <see langword="true"/> when the tool call is authorized and may run its side effects;
    /// <see langword="false"/> when it must be refused.
    /// </summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// The host-authored, model-visible denial reason, or <c>null</c>. Meaningful only when
    /// <see cref="IsAllowed"/> is <see langword="false"/>.
    /// </summary>
    public string? Reason { get; }
}
