using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SharpCoder;

/// <summary>
/// The single authorization boundary SharpCoder puts in front of every locally invokable tool it
/// registers in <see cref="ChatOptions.Tools"/>.
/// <para>
/// One <c>AuthorizedAIFunction</c> wraps one <see cref="AIFunction"/>. It runs the host's
/// <see cref="AgentOptions.AuthorizeTool"/> callback BEFORE the wrapped function can produce any side
/// effect, and either invokes the wrapped function unchanged (allow) or returns a fixed,
/// model-consumable refusal (deny, missing/invalid decision, or a faulting callback). The wrapper
/// keeps the wrapped function's name, description, JSON schemas, additional properties and
/// underlying method, so schema generation, reserved-name checks and diagnostics see exactly the
/// same tool surface as without authorization.
/// </para>
/// </summary>
internal static class ToolAuthorizationGuard
{
    /// <summary>
    /// The refusal the model sees when the host denied the call and supplied no usable reason.
    /// </summary>
    internal const string DeniedRefusalFormat = "Tool '{0}' was not executed: the host denied this tool call.";

    /// <summary>
    /// The refusal the model sees when the host denied the call with a reason. The reason is
    /// host-authored, model-visible text and is inserted verbatim.
    /// </summary>
    internal const string DeniedWithReasonRefusalFormat =
        "Tool '{0}' was not executed: the host denied this tool call. Reason: {1}";

    /// <summary>
    /// The refusal the model sees when the authorization callback threw. It never contains the
    /// exception's type, message, stack trace, or any other detail of the fault: the run fails
    /// CLOSED with this fixed text.
    /// </summary>
    internal const string FaultedRefusalFormat =
        "Tool '{0}' was not executed: the call could not be authorized by the host.";

    /// <summary>The shared empty argument snapshot used for tools that take no arguments.</summary>
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(0, StringComparer.Ordinal));

    /// <summary>
    /// Test seam: true when <paramref name="tool"/> is an authorization wrapper produced by
    /// <see cref="Wrap"/> — false for every tool registered as-is (the no-callback shape) and for a
    /// host's own tool instances.
    /// </summary>
    internal static bool IsGuarded(AITool? tool) => tool is AuthorizedAIFunction;

    /// <summary>
    /// Test seam: returns the function inside <paramref name="tool"/>'s authorization wrapper, or
    /// <c>null</c> when it is not guarded. The identity of the returned instance is what SharpCoder
    /// would have registered without an authorization callback.
    /// </summary>
    internal static AIFunction? Unwrap(AITool? tool) => (tool as AuthorizedAIFunction)?.Inner;

    /// <summary>
    /// Wraps <paramref name="function"/> in the authorization boundary.
    /// </summary>
    /// <param name="function">The locally invokable function to guard.</param>
    /// <param name="authorize">The callback captured when the agent was constructed.</param>
    /// <param name="correlationId">The host correlation copied when the agent was constructed.</param>
    /// <param name="logger">Logger for decision/fault diagnostics (never model-visible).</param>
    internal static AIFunction Wrap(
        AIFunction function,
        Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> authorize,
        string? correlationId,
        ILogger logger)
    {
        if (function is null) throw new ArgumentNullException(nameof(function));
        if (authorize is null) throw new ArgumentNullException(nameof(authorize));
        return new AuthorizedAIFunction(function, authorize, correlationId, logger);
    }

    /// <summary>
    /// Builds the read-only, defensively copied argument snapshot handed to the host callback.
    /// <see cref="JsonElement"/> values (what providers and <see cref="FunctionInvokingChatClient"/>
    /// usually supply) are converted to plain CLR values, so a host can compare a
    /// <c>filePath</c> or <c>command</c> argument directly. Nested arrays and objects are copied
    /// recursively; everything else is copied by reference, exactly as the model supplied it.
    /// <para>
    /// This method never throws: a value that cannot be read (for example an element whose backing
    /// <see cref="JsonDocument"/> was disposed) is passed through as-is, so building the snapshot can
    /// never prevent the host callback from running and deciding.
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> SnapshotArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return EmptyArguments;

        var copy = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var argument in arguments)
            copy[argument.Key] = SnapshotValue(argument.Value);

        return new ReadOnlyDictionary<string, object?>(copy);
    }

    private static object? SnapshotValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement element:
                try { return FromJsonElement(element); }
                catch (Exception) { return value; }
            case JsonDocument document:
                try { return FromJsonElement(document.RootElement); }
                catch (Exception) { return value; }
            default:
                return value;
        }
    }

    private static object? FromJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) return integer;
                if (element.TryGetDecimal(out var number)) return number;
                return element.GetDouble();
            case JsonValueKind.Array:
                var items = new object?[element.GetArrayLength()];
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    items[index++] = FromJsonElement(item);
                return items;
            case JsonValueKind.Object:
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    properties[property.Name] = FromJsonElement(property.Value);
                return new ReadOnlyDictionary<string, object?>(properties);
            default:
                return element.GetRawText();
        }
    }

    /// <summary>
    /// An <see cref="AIFunction"/> that authorizes each invocation before delegating to the wrapped
    /// function. Only SharpCoder creates these.
    /// </summary>
    private sealed class AuthorizedAIFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> _authorize;
        private readonly string? _correlationId;
        private readonly ILogger _logger;

        internal AuthorizedAIFunction(
            AIFunction inner,
            Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> authorize,
            string? correlationId,
            ILogger logger)
        {
            _inner = inner;
            _authorize = authorize;
            _correlationId = correlationId;
            _logger = logger;
        }

        /// <summary>The guarded function — the same instance SharpCoder would have registered.</summary>
        internal AIFunction Inner => _inner;

        public override string Name => _inner.Name;

        public override string Description => _inner.Description;

        public override JsonElement JsonSchema => _inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

        public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;

        public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

        /// <summary>
        /// Answers unkeyed tool/function type queries with this guard — matching the default
        /// <see cref="AITool.GetService(Type, object?)"/> behavior, where a tool returns itself — so
        /// nobody obtains the unwrapped function through the service channel. Every other request is
        /// forwarded to the wrapped function, so auxiliary services it exposes stay reachable.
        /// </summary>
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
            if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
            return _inner.GetService(serviceType, serviceKey);
        }

        public override string ToString() => _inner.ToString() ?? Name;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // The caller's cancellation is honored before ANY side effect, including before the
            // host callback runs.
            cancellationToken.ThrowIfCancellationRequested();

            ToolAuthorizationDecision? decision;
            try
            {
                var context = new ToolAuthorizationContext(
                    Name,
                    _correlationId,
                    SnapshotArguments(arguments));

                decision = await _authorize(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation keeps its identity: it is never converted into a refusal.
                throw;
            }
            catch (Exception ex)
            {
                // Fail CLOSED. The model only ever sees the fixed refusal below; the fault's
                // details go to the host log, which the model cannot read.
                LogFault(ex);
                return string.Format(FaultedRefusalFormat, Name);
            }

            if (decision is not null && decision.IsAllowed)
            {
                // Allowed: the wrapped function runs completely unchanged, with the very same
                // arguments instance and cancellation token the caller supplied.
                return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }

            var reason = decision?.Reason;
            if (string.IsNullOrWhiteSpace(reason))
            {
                LogDenied(reason: null);
                return string.Format(DeniedRefusalFormat, Name);
            }

            LogDenied(reason);
            return string.Format(DeniedWithReasonRefusalFormat, Name, reason);
        }

        /// <summary>Records a denial; never throws, so a failing logger cannot change the outcome.</summary>
        private void LogDenied(string? reason)
        {
            try
            {
                _logger.LogInformation(
                    "Tool {ToolName} was denied by the host authorization callback (correlation: {CorrelationId}, reason supplied: {ReasonSupplied}).",
                    Name,
                    _correlationId,
                    reason is not null);
            }
            catch
            {
                // Diagnostics never change behavior.
            }
        }

        /// <summary>Records a callback fault; never throws, so a failing logger cannot turn a fail-closed refusal into a crash.</summary>
        private void LogFault(Exception ex)
        {
            try
            {
                _logger.LogWarning(
                    ex,
                    "The host authorization callback threw for tool {ToolName} (correlation: {CorrelationId}); the call was refused.",
                    Name,
                    _correlationId);
            }
            catch
            {
                // Diagnostics never change behavior.
            }
        }
    }
}
