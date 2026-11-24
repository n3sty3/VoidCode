using System;
using System.IO;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

public class CrossPlatformTests
{
    [Fact]
    public void BridgeProtocol_AcceptsCustomLoopbackDocumentUri_ForMacPhotinoHost()
    {
        var docUri = new Uri("http://127.0.0.1:49201/index.html");
        var nonce = BridgeProtocol.CreateNonce();
        var json = $"{{\"action\":\"get_init_state\",\"id\":\"req_1\",\"bridgeNonce\":\"{nonce}\"}}";

        Assert.True(BridgeProtocol.IsTrustedSource("http://127.0.0.1:49201/index.html", docUri));
        Assert.False(BridgeProtocol.IsTrustedSource("http://127.0.0.1:49202/index.html", docUri));
        Assert.False(BridgeProtocol.IsTrustedSource("http://evil.example/index.html", docUri));

        var ok = BridgeProtocol.TryAccept("http://127.0.0.1:49201/index.html", json, nonce, out var msg, out var reason, docUri);
        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("get_init_state", msg!["action"]!.GetValue<string>());
    }

    [Fact]
    public void InspectBinary_DetectsMachO64Arm64Binary()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"voidcode_macho_{Guid.NewGuid():N}.dylib");
        try
        {
            // Minimal valid Mach-O 64-bit ARM64 MH_DYLIB header (32 bytes)
            var data = new byte[64];
            data[0] = 0xCF; data[1] = 0xFA; data[2] = 0xED; data[3] = 0xFE; // MH_MAGIC_64
            BitConverter.GetBytes(0x0100000Cu).CopyTo(data, 4); // CPU_TYPE_ARM64
            BitConverter.GetBytes(6u).CopyTo(data, 12);         // MH_DYLIB
            BitConverter.GetBytes(2u).CopyTo(data, 16);         // ncmds
            BitConverter.GetBytes(0x00200085u).CopyTo(data, 24);// flags
            File.WriteAllBytes(tmp, data);

            var report = FileSystemService.InspectBinary(tmp);
            Assert.Contains("MACH-O HEADER (macOS)", report);
            Assert.Contains("Mach-O 64-bit", report);
            Assert.Contains("ARM64 (Apple Silicon)", report);
            Assert.Contains("MH_DYLIB", report);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void InspectBinary_DetectsMachOUniversalFatBinary()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"voidcode_fat_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[32];
            data[0] = 0xCA; data[1] = 0xFE; data[2] = 0xBA; data[3] = 0xBE; // FAT_MAGIC
            data[7] = 2; // 2 slices (arm64 + x86_64)
            File.WriteAllBytes(tmp, data);

            var report = FileSystemService.InspectBinary(tmp);
            Assert.Contains("Mach-O Universal / Fat Binary (2 architecture slice(s))", report);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void PlatformServices_ReturnsValidPlatformIdAndDefaults()
    {
        Assert.Contains(PlatformServices.PlatformId, new[] { "windows", "macos", "linux", "unknown" });
        Assert.False(string.IsNullOrWhiteSpace(PlatformServices.DefaultShellType));
        Assert.EndsWith("VoidCode", PlatformServices.GetDefaultAppDataRoot(), StringComparison.OrdinalIgnoreCase);
    }
}
