using System;

namespace SharpCoder.SubAgents;

/// <summary>
/// Describes a model available to sub-agents.
/// </summary>
public sealed record SubAgentModelInfo
{
    /// <summary>Creates a new model descriptor.</summary>
    /// <param name="id">The model identifier. Must not be null or whitespace.</param>
    /// <param name="description">Optional human/LLM-readable description.</param>
    /// <param name="contextWindow">Optional context window size in tokens.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or whitespace.</exception>
    public SubAgentModelInfo(string id, string? description = null, int? contextWindow = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Model id cannot be null or whitespace.", nameof(id));
        Id = id;
        Description = description;
        ContextWindow = contextWindow;
    }

    /// <summary>Creates a new model descriptor with explicit vision support.</summary>
    /// <param name="id">The model identifier. Must not be null or whitespace.</param>
    /// <param name="description">Optional human/LLM-readable description.</param>
    /// <param name="contextWindow">Optional context window size in tokens.</param>
    /// <param name="supportsVision">
    /// Informational flag indicating whether the model is vision-capable. Hosts use this to guide
    /// image/PDF <c>image_paths</c> model selection; SharpCoder does not enforce it.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or whitespace.</exception>
    public SubAgentModelInfo(string id, string? description, int? contextWindow, bool supportsVision)
        : this(id, description, contextWindow)
    {
        SupportsVision = supportsVision;
    }

    /// <summary>The model identifier.</summary>
    public string Id { get; }

    /// <summary>Optional description of the model.</summary>
    public string? Description { get; }

    /// <summary>Optional context window size in tokens.</summary>
    public int? ContextWindow { get; }

    /// <summary>
    /// Gets a value indicating whether the model is vision-capable. This is informational only:
    /// hosts use it to tell the LLM which models support image/PDF <c>image_paths</c>, but
    /// SharpCoder does not reject visual inputs when this flag is <see langword="false"/>.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool SupportsVision { get; } = false;
}
