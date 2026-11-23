using System;
using System.Text;

namespace VoidCode.Agent.Tools;

/// <summary>
/// Single truncation and untrusted-content policy shared by every agent tool.
/// Keeps both the head (context/headers) and tail (final error/summary) instead of tail-chopping,
/// And wraps external or file payloads in explicit untrusted-content boundaries.
/// </summary>
internal static class ToolOutputPolicy
{
    internal const int ReadFileMaxChars = 20000;
    internal const int CommandMaxChars = 12000;
    internal const int DiagnosticsMaxChars = 12000;
    internal const int GitDiffMaxChars = 10000;
    internal const int WebFetchMaxChars = 10000;
    internal const int SearchMaxChars = 12000;

    internal static string Truncate(string? text, int maxChars, string? hint = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (maxChars <= 0 || text.Length <= maxChars) return text;

        // Keep 60% head + 40% tail so headers and final error lines both survive.
        const int markerReserve = 120;
        if (maxChars <= markerReserve)
        {
            var omittedBytes = Encoding.UTF8.GetByteCount(text.AsSpan(maxChars));
            return text[..maxChars] + $"... [{omittedBytes:N0} bytes omitted]";
        }

        var usable = maxChars - markerReserve;
        var headLen = (int)(usable * 0.6);
        var tailLen = usable - headLen;

        var omittedSpan = text.AsSpan(headLen, text.Length - headLen - tailLen);
        var omittedByteCount = Encoding.UTF8.GetByteCount(omittedSpan);
        var suffix = string.IsNullOrWhiteSpace(hint) ? string.Empty : $"; {hint}";

        var sb = new StringBuilder(maxChars + 64);
        sb.Append(text, 0, headLen);
        sb.Append("\n... [");
        sb.Append(omittedByteCount.ToString("N0"));
        sb.Append(" bytes omitted");
        sb.Append(suffix);
        sb.Append("] ...\n");
        sb.Append(text, text.Length - tailLen, tailLen);
        return sb.ToString();
    }

    internal static string WrapUntrusted(string source, string content)
    {
        var safeSource = (source ?? "external").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
        // Escape any literal closing tag in untrusted text so payloads cannot prematurely break out.
        var safeContent = (content ?? string.Empty).Replace("</untrusted_content>", "</untrusted_content_escaped>");
        return $"<untrusted_content source=\"{safeSource}\">\n{safeContent}\n</untrusted_content>";
    }
}
