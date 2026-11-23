using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

/// <summary>
/// The LSP base-protocol framing
/// Loop so it can be tested with a stream. This is the part of an LSP client that actually breaks in the
/// Field - headers split across reads, two messages in one buffer, a payload whose length is counted in
/// Bytes but decoded as characters - and none of it could be exercised while it lived inside a loop
/// Reading a live child process's stdout.
///
/// The wire format: <c>Content-Length: N\r\n</c> (+ optional headers) <c>\r\n</c> then exactly N UTF-8
/// Bytes of JSON.
/// </summary>
internal static class LspFraming
{
    /// <summary>Encodes one message: the header, the separator and the payload as UTF-8 bytes.</summary>
    internal static byte[] EncodeFrame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var frame = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(payload, 0, frame, header.Length, payload.Length);
        return frame;
    }

    /// <summary>
    /// Reads one message from <paramref name="stream"/>. Returns the JSON payload, or null at end of
    /// Stream. A message whose <c>Content-Length</c> is missing or unparseable is skipped rather than
    /// Thrown away with the rest of the stream: the next header is read instead.
    /// </summary>
    internal static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            int contentLength = -1;

            while (true)
            {
                var line = await ReadLineAsciiAsync(stream, cancellationToken);
                if (line == null) return null; // EOF, even mid-header
                if (string.IsNullOrEmpty(line)) break; // Empty line separates headers from payload

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring("Content-Length:".Length).Trim();
                    int.TryParse(value, out contentLength);
                }
            }

            // Anything else in the header block (Content-Type today) is ignored on purpose. A message
            // Without a usable length is skipped and the next header block is read from the same stream.
            if (contentLength <= 0) continue;

            var buffer = new byte[contentLength];
            int totalRead = 0;
            while (totalRead < contentLength)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), cancellationToken);
                if (read <= 0) return null; // stream ended mid-payload
                totalRead += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, contentLength);
        }
    }

    /// <summary>Reads one CRLF-terminated ASCII header line; null at EOF. A lone LF ends the line too.</summary>
    private static async Task<string?> ReadLineAsciiAsync(Stream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read <= 0) return sb.Length > 0 ? sb.ToString() : null;
            if (one[0] == (byte)'\n') return sb.ToString();
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            // LSP headers are tiny; an unterminated line this long is a broken stream, not a header.
            if (sb.Length > 8192) return null;
        }
    }
}