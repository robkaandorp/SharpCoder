using System.Text;
using Markdig;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// How the CLI renders assistant markdown in the terminal.
/// </summary>
public enum MarkdownMode
{
    /// <summary><see cref="Live"/> on an interactive terminal, <see cref="Raw"/> when output is redirected.</summary>
    Auto,

    /// <summary>Buffer each block and render it as soon as it completes.</summary>
    Live,

    /// <summary>Buffer the whole turn and render it once, at the end.</summary>
    Full,

    /// <summary>No markdown processing at all; raw bytes to the terminal (previous behavior).</summary>
    Raw,
}
