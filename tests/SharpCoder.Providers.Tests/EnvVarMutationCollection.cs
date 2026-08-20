namespace SharpCoder.Providers.Tests;

/// <summary>
/// Serializes tests that mutate process-wide environment variables (e.g.
/// <c>SHARPCODER_DIAGNOSTICS_DIR</c>, <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>) so they cannot corrupt
/// the environment observed by parallel tests.
/// </summary>
[CollectionDefinition("EnvVarMutation", DisableParallelization = true)]
public sealed class EnvVarMutationCollection { }
