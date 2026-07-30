using System;

namespace SharpCoder;

/// <summary>
/// An image (or PDF) attachment supplied alongside a user message, carrying the
/// raw bytes plus the media type needed to build a multimodal chat request.
/// </summary>
public sealed class ImageAttachment
{
    /// <summary>The raw attachment bytes.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>The IANA media type of <see cref="Data"/> (for example <c>image/png</c>).</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>An optional display name — typically the path the attachment was loaded from.</summary>
    public string? Name { get; set; }
}
