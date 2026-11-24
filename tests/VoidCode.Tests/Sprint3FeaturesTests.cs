using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Mcp;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

public class Sprint3FeaturesTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request));
    }

    [Fact]
    public async Task McpClient_StreamableHttpTransport_InitializesListsAndCallsToolsOverSseAndJson()
    {
        using var http = new HttpClient(new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var node = JsonNode.Parse(body)!.AsObject();
            var method = node["method"]?.GetValue<string>() ?? "";
            var id = node["id"]?.GetValue<int>() ?? 0;

            if (method == "initialize")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"2024-11-05\"}}}}", Encoding.UTF8, "application/json")
                };
                resp.Headers.Add("Mcp-Session-Id", "sess-http-42");
                return resp;
            }

            if (method == "tools/list")
            {
                // Return SSE text/event-stream frame to verify SSE parsing too
                var sse = $"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"tools\":[{{\"name\":\"query_docs\",\"description\":\"Lookup API docs\",\"inputSchema\":{{\"type\":\"object\"}}}}]}}}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            if (method == "tools/call")
            {
                Assert.True(req.Headers.Contains("Mcp-Session-Id"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":\"Docs for Iced\"}}]}}}}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        using var client = new McpClient("context7", "https://mcp.context7.com/mcp", new Dictionary<string, string> { ["X-Test"] = "1" }, http);
        Assert.True(await client.InitializeAsync());
        var tools = await client.ListToolsAsync();
        var tool = Assert.Single(tools);
        Assert.Equal("mcp__context7__query_docs", tool.QualifiedName);

        var (ok, output) = await client.CallToolAsync("query_docs", new JsonObject { ["q"] = "Iced" });
        Assert.True(ok);
        Assert.Contains("Docs for Iced", output);
        Assert.Contains("<untrusted_content source=\"mcp:context7/query_docs\">", output);
    }

    [Fact]
    public void McpGalleryService_InstallsListsAndRemovesPresetsInWorkspaceMcpJson()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"voidcode_mcpgal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            var initial = McpGalleryService.List(ws);
            Assert.Contains(initial, i => i.Id == "context7" && i.Transport == "http" && !i.Installed);

            var inst = McpGalleryService.Install(ws, "context7");
            Assert.True(inst.Ok, inst.Message);
            Assert.Contains(inst.Items, i => i.Id == "context7" && i.Installed);

            var custom = McpGalleryService.Install(ws, "my-remote", customUrl: "https://mcp.example.dev/rpc");
            Assert.True(custom.Ok, custom.Message);
            Assert.Contains(custom.Items, i => i.Id == "my-remote" && i.Installed && i.Transport == "http");

            var loaded = McpManager.LoadConfig(ws);
            Assert.Contains(loaded, c => c.Name == "context7" && c.IsHttp && c.Url == "https://mcp.context7.com/mcp");
            Assert.Contains(loaded, c => c.Name == "my-remote" && c.IsHttp && c.Url == "https://mcp.example.dev/rpc");

            var removed = McpGalleryService.Remove(ws, "context7");
            Assert.True(removed.Ok);
            Assert.Contains(removed.Items, i => i.Id == "context7" && !i.Installed);
        }
        finally
        {
            McpManager.ShutdownAll();
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void BinaryWorkbenchService_DisassemblesX86_64InstructionsAndBuildsHexDump()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"voidcode_bw_{Guid.NewGuid():N}.bin");
        try
        {
            // Real x86-64 machine code:
            //   55             push rbp
            //   48 89 E5       mov rbp, rsp
            //   B8 2A 00 00 00 mov eax, 2Ah
            //   5D             pop rbp
            //   C3             ret
            var code = new byte[] { 0x55, 0x48, 0x89, 0xE5, 0xB8, 0x2A, 0x00, 0x00, 0x00, 0x5D, 0xC3 };
            File.WriteAllBytes(tmp, code);

            var report = BinaryWorkbenchService.Inspect(tmp, hexOffset: 0, hexLength: 64, maxInstructions: 20);
            Assert.True(report.Ok, report.Error);
            Assert.Equal(64, report.Bitness);
            Assert.NotEmpty(report.HexRows);
            Assert.Contains("55 48 89 E5", report.HexRows[0].Hex);

            Assert.True(report.Disassembly.Count >= 5);
            Assert.Contains("push", report.Disassembly[0].Instruction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mov", report.Disassembly[1].Instruction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2Ah", report.Disassembly[2].Instruction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ret", report.Disassembly[4].Instruction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
