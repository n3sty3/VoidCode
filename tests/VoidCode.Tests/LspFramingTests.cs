using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// LSP base-protocol framing, tested against streams instead of a language server.
// These are the cases that break real clients in the field: a header split across reads, two messages in
// One buffer, and a body whose Content-Length counts bytes while the payload is multi-byte UTF-8.
public class LspFramingTests
{
    private static async Task<string?> ReadOne(byte[] wire, int maxChunk = int.MaxValue)
    {
        using var stream = new ChunkedStream(wire, maxChunk);
        return await LspFraming.ReadMessageAsync(stream, CancellationToken.None);
    }

    [Fact]
    public void EncodeFrame_PutsTheByteLengthInTheHeader()
    {
        var frame = LspFraming.EncodeFrame("{\"jsonrpc\":\"2.0\"}");
        var text = Encoding.UTF8.GetString(frame);

        Assert.StartsWith("Content-Length: 17\r\n\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("{\"jsonrpc\":\"2.0\"}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMessage_RoundTripsWhatWasEncoded()
    {
        const string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}";

        Assert.Equal(json, await ReadOne(LspFraming.EncodeFrame(json)));
    }

    [Fact]
    public async Task ReadMessage_CountsBytesNotCharacters()
    {
        // "é" and "✓" are 2 and 3 bytes; a reader that used json.Length as the header value would cut
        // The payload short and every later message would be misaligned.
        const string json = "{\"message\":\"café ✓ grüße\"}";
        var frame = LspFraming.EncodeFrame(json);
        var header = Encoding.ASCII.GetString(frame, 0, Array.IndexOf(frame, (byte)'{'));
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(json)}", header, StringComparison.Ordinal);

        Assert.Equal(json, await ReadOne(frame));
    }

    [Fact]
    public async Task ReadMessage_HandlesTwoMessagesBackToBack()
    {
        var wire = LspFraming.EncodeFrame("{\"id\":1}")
            .Concat(LspFraming.EncodeFrame("{\"id\":2}"))
            .ToArray();

        using var stream = new MemoryStream(wire);
        Assert.Equal("{\"id\":1}", await LspFraming.ReadMessageAsync(stream, CancellationToken.None));
        Assert.Equal("{\"id\":2}", await LspFraming.ReadMessageAsync(stream, CancellationToken.None));
        Assert.Null(await LspFraming.ReadMessageAsync(stream, CancellationToken.None)); // EOF
    }

    [Fact]
    public async Task ReadMessage_HandlesAStreamThatDribblesOneByteAtATime()
    {
        var wire = LspFraming.EncodeFrame("{\"id\":7,\"result\":\"0123456789\"}");

        Assert.Equal("{\"id\":7,\"result\":\"0123456789\"}", await ReadOne(wire, maxChunk: 1));
        Assert.Equal("{\"id\":7,\"result\":\"0123456789\"}", await ReadOne(wire, maxChunk: 3));
        Assert.Equal("{\"id\":7,\"result\":\"0123456789\"}", await ReadOne(wire, maxChunk: 17));
    }

    [Fact]
    public async Task ReadMessage_AcceptsExtraHeadersAndAnyHeaderCasing()
    {
        const string json = "{\"id\":9}";
        var wire = Encoding.UTF8.GetBytes(
            $"content-length: {Encoding.UTF8.GetByteCount(json)}\r\n" +
            "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n" + json);

        Assert.Equal(json, await ReadOne(wire));
    }

    [Fact]
    public async Task ReadMessage_SkipsAMessageWithoutAUsableLength()
    {
        // A broken header block followed by a good message: the reader must resynchronise, not throw.
        const string json = "{\"id\":3}";
        var wire = Encoding.UTF8.GetBytes("Content-Length: not-a-number\r\n\r\n")
            .Concat(LspFraming.EncodeFrame(json))
            .ToArray();

        Assert.Equal(json, await ReadOne(wire));
    }

    [Fact]
    public async Task ReadMessage_TreatsATruncatedPayloadAsEndOfStream()
    {
        var frame = LspFraming.EncodeFrame("{\"id\":42,\"result\":\"0123456789\"}");

        Assert.Null(await ReadOne(frame[..(frame.Length - 4)]));
    }

    [Fact]
    public async Task ReadMessage_ReturnsNullForAnEmptyStream()
    {
        Assert.Null(await ReadOne(Array.Empty<byte>()));
    }

    /// <summary>A stream that never returns more than <see cref="_maxChunk"/> bytes per read.</summary>
    private sealed class ChunkedStream : MemoryStream
    {
        private readonly int _maxChunk;

        public ChunkedStream(byte[] buffer, int maxChunk) : base(buffer) => _maxChunk = maxChunk;

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
            => base.ReadAsync(destination[..Math.Min(destination.Length, _maxChunk)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => base.ReadAsync(buffer, offset, Math.Min(count, _maxChunk), cancellationToken);
    }
}