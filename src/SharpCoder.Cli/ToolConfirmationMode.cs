namespace SharpCoder.Cli;

/// <summary>How the CLI asks the human before the agent runs a sensitive tool.</summary>
public enum ToolConfirmationMode
{
    /// <summary>
    /// Prompt on an interactive terminal; auto-allow when input or output is redirected so scripted
    /// one-shot runs never hang. This is the default.
    /// </summary>
    Auto = 0,

    /// <summary>Always ask, even when input is redirected (an EOF answer denies the call — fail closed).</summary>
    Always = 1,

    /// <summary>Never ask; every guarded tool call is allowed. Intended for CI and trusted batches.</summary>
    Never = 2,
}
