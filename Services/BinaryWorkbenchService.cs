using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

namespace VoidCode.Services;

public sealed class HexDumpRow
{
    [JsonPropertyName("offset")]
    public string Offset { get; init; } = "";

    [JsonPropertyName("hex")]
    public string Hex { get; init; } = "";

    [JsonPropertyName("ascii")]
    public string Ascii { get; init; } = "";
}

public sealed class DisassemblyRow
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("bytes")]
    public string Bytes { get; init; } = "";

    [JsonPropertyName("instruction")]
    public string Instruction { get; init; } = "";
}

public sealed class BinaryWorkbenchReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }

    [JsonPropertyName("architecture")]
    public string Architecture { get; init; } = "";

    [JsonPropertyName("bitness")]
    public int Bitness { get; init; } = 64;

    [JsonPropertyName("codeStartOffset")]
    public int CodeStartOffset { get; init; }

    [JsonPropertyName("codeVirtualAddress")]
    public string CodeVirtualAddress { get; init; } = "";

    [JsonPropertyName("summaryText")]
    public string SummaryText { get; init; } = "";

    [JsonPropertyName("hexRows")]
    public List<HexDumpRow> HexRows { get; init; } = new();

    [JsonPropertyName("disassembly")]
    public List<DisassemblyRow> Disassembly { get; init; } = new();
}

/// <summary>
/// Interactive Binary Reverse-Engineering Workbench powered by managed <c>Iced</c> x86/x86-64 disassembly,
/// paginated Hex+ASCII inspection, and PE / Mach-O / ELF header parsing.
/// </summary>
public static class BinaryWorkbenchService
{
    private const int MaxReadBytes = 2 * 1024 * 1024; // Cap single inspection read at 2 MB

    public static BinaryWorkbenchReport Inspect(
        string filePath,
        int hexOffset = 0,
        int hexLength = 512,
        int maxInstructions = 120)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new BinaryWorkbenchReport { Ok = false, Path = filePath ?? "", Error = "Binary file does not exist." };
        }

        try
        {
            var info = new FileInfo(filePath);
            int toRead = (int)Math.Min(info.Length, MaxReadBytes);
            var bytes = new byte[toRead];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int total = 0;
                while (total < toRead)
                {
                    int n = fs.Read(bytes, total, toRead - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total < toRead) Array.Resize(ref bytes, total);
            }

            var (arch, bitness, codeOffset, codeVa, canDisassembleX86) = DetectCodeSection(bytes);
            int clampedHexOffset = Math.Clamp(hexOffset, 0, Math.Max(0, bytes.Length - 1));
            int clampedHexLen = Math.Clamp(hexLength, 16, 4096);
            var hexRows = BuildHexDump(bytes, clampedHexOffset, clampedHexLen);

            var disasm = canDisassembleX86 && bytes.Length > codeOffset
                ? DisassembleX86(bytes, codeOffset, codeVa, bitness, Math.Clamp(maxInstructions, 1, 400))
                : new List<DisassemblyRow>();

            var summaryText = FileSystemService.InspectBinary(filePath);

            return new BinaryWorkbenchReport
            {
                Ok = true,
                Path = filePath,
                FileSize = info.Length,
                Architecture = arch,
                Bitness = bitness,
                CodeStartOffset = codeOffset,
                CodeVirtualAddress = $"0x{codeVa:X}",
                SummaryText = summaryText,
                HexRows = hexRows,
                Disassembly = disasm
            };
        }
        catch (Exception ex)
        {
            return new BinaryWorkbenchReport { Ok = false, Path = filePath, Error = $"Failed to inspect binary: {ex.Message}" };
        }
    }

    public static List<DisassemblyRow> DisassembleX86(
        byte[] bytes,
        int startOffset,
        ulong baseVirtualAddress,
        int bitness = 64,
        int maxInstructions = 120)
    {
        var rows = new List<DisassemblyRow>();
        if (bytes.Length == 0 || startOffset < 0 || startOffset >= bytes.Length) return rows;

        int sliceLen = Math.Min(bytes.Length - startOffset, maxInstructions * 15);
        var slice = new byte[sliceLen];
        Buffer.BlockCopy(bytes, startOffset, slice, 0, sliceLen);

        var codeReader = new ByteArrayCodeReader(slice);
        var decoder = Decoder.Create(bitness == 32 ? 32 : 64, codeReader, baseVirtualAddress);
        var formatter = new IntelFormatter();
        formatter.Options.SpaceAfterOperandSeparator = true;
        var output = new StringOutput();

        ulong endRip = baseVirtualAddress + (ulong)slice.Length;
        while (decoder.IP < endRip && rows.Count < maxInstructions)
        {
            var instr = decoder.Decode();
            output.Reset();
            formatter.Format(instr, output);

            int relIdx = (int)(instr.IP - baseVirtualAddress);
            var bytesSb = new StringBuilder(instr.Length * 3);
            for (int i = 0; i < instr.Length && relIdx + i < slice.Length; i++)
            {
                if (i > 0) bytesSb.Append(' ');
                bytesSb.Append(slice[relIdx + i].ToString("X2"));
            }

            rows.Add(new DisassemblyRow
            {
                Address = bitness == 32 ? $"0x{instr.IP:X8}" : $"0x{instr.IP:X16}",
                Bytes = bytesSb.ToString(),
                Instruction = output.ToString()
            });
        }

        return rows;
    }

    public static List<HexDumpRow> BuildHexDump(byte[] bytes, int offset, int length)
    {
        var rows = new List<HexDumpRow>();
        if (bytes.Length == 0) return rows;

        int end = Math.Min(bytes.Length, offset + length);
        for (int pos = offset; pos < end; pos += 16)
        {
            int count = Math.Min(16, end - pos);
            var hexSb = new StringBuilder(48);
            var asciiSb = new StringBuilder(16);
            for (int i = 0; i < 16; i++)
            {
                if (i == 8) hexSb.Append(' ');
                if (i < count)
                {
                    byte b = bytes[pos + i];
                    hexSb.Append(b.ToString("X2")).Append(' ');
                    asciiSb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                else
                {
                    hexSb.Append("   ");
                }
            }
            rows.Add(new HexDumpRow
            {
                Offset = $"0x{pos:X8}",
                Hex = hexSb.ToString().TrimEnd(),
                Ascii = asciiSb.ToString()
            });
        }

        return rows;
    }

    private static (string arch, int bitness, int codeOffset, ulong codeVa, bool canDisassembleX86) DetectCodeSection(byte[] bytes)
    {
        // PE32 / PE32+ executable
        if (bytes.Length >= 64 && bytes[0] == 'M' && bytes[1] == 'Z')
        {
            int peOffset = BitConverter.ToInt32(bytes, 0x3C);
            if (peOffset > 0 && peOffset + 24 <= bytes.Length &&
                bytes[peOffset] == 'P' && bytes[peOffset + 1] == 'E' && bytes[peOffset + 2] == 0 && bytes[peOffset + 3] == 0)
            {
                ushort machine = BitConverter.ToUInt16(bytes, peOffset + 4);
                ushort numSections = BitConverter.ToUInt16(bytes, peOffset + 6);
                ushort optHeaderSize = BitConverter.ToUInt16(bytes, peOffset + 20);
                int optOffset = peOffset + 24;

                bool isX64 = machine == 0x8664;
                bool isX86 = machine == 0x014C;
                int bitness = isX86 ? 32 : 64;
                string arch = isX64 ? "PE x86_64" : isX86 ? "PE x86" : (machine == 0xAA64 ? "PE ARM64" : $"PE 0x{machine:X4}");

                ulong imageBase = 0x140000000UL;
                uint entryRva = 0;
                if (optHeaderSize >= 32 && optOffset + optHeaderSize <= bytes.Length)
                {
                    ushort magic = BitConverter.ToUInt16(bytes, optOffset);
                    bool isPe32Plus = magic == 0x20B;
                    entryRva = BitConverter.ToUInt32(bytes, optOffset + 16);
                    imageBase = isPe32Plus
                        ? BitConverter.ToUInt64(bytes, optOffset + 24)
                        : BitConverter.ToUInt32(bytes, optOffset + 28);
                }

                // Locate the section containing EntryPoint (or fallback to .text / first executable section)
                int secTableOffset = optOffset + optHeaderSize;
                int bestRawOffset = 0;
                ulong bestVa = imageBase;
                if (numSections > 0 && secTableOffset + numSections * 40 <= bytes.Length)
                {
                    for (int s = 0; s < numSections; s++)
                    {
                        int so = secTableOffset + s * 40;
                        uint virtSize = BitConverter.ToUInt32(bytes, so + 8);
                        uint virtAddr = BitConverter.ToUInt32(bytes, so + 12);
                        uint rawSize = BitConverter.ToUInt32(bytes, so + 16);
                        uint rawPtr = BitConverter.ToUInt32(bytes, so + 20);
                        uint chars = BitConverter.ToUInt32(bytes, so + 36);

                        if (entryRva >= virtAddr && entryRva < virtAddr + Math.Max(virtSize, rawSize))
                        {
                            int delta = (int)(entryRva - virtAddr);
                            if (rawPtr + delta < bytes.Length)
                            {
                                return (arch, bitness, (int)rawPtr + delta, imageBase + entryRva, isX64 || isX86);
                            }
                        }

                        if (bestRawOffset == 0 && (chars & 0x20000000) != 0 && rawPtr > 0 && rawPtr < bytes.Length)
                        {
                            bestRawOffset = (int)rawPtr;
                            bestVa = imageBase + virtAddr;
                        }
                    }
                }

                return (arch, bitness, bestRawOffset, bestVa, isX64 || isX86);
            }
        }

        // Mach-O 64-bit
        if (bytes.Length >= 28 && bytes[0] == 0xCF && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] == 0xFE)
        {
            uint cpuType = BitConverter.ToUInt32(bytes, 4);
            bool isX64 = cpuType == 0x01000007;
            string arch = cpuType == 0x0100000C ? "Mach-O ARM64" : isX64 ? "Mach-O x86_64" : $"Mach-O 0x{cpuType:X8}";
            return (arch, 64, 0, 0x100000000UL, isX64);
        }

        // ELF
        if (bytes.Length >= 20 && bytes[0] == 0x7F && bytes[1] == 'E' && bytes[2] == 'L' && bytes[3] == 'F')
        {
            bool is64 = bytes[4] == 2;
            ushort eMachine = BitConverter.ToUInt16(bytes, 18);
            bool isX86Family = eMachine == 0x3E || eMachine == 0x03;
            string arch = eMachine == 0x3E ? "ELF x86_64" : eMachine == 0xB7 ? "ELF ARM64" : eMachine == 0x03 ? "ELF x86" : $"ELF 0x{eMachine:X}";
            return (arch, is64 ? 64 : 32, 0, 0x400000UL, isX86Family);
        }

        // Raw binary / shellcode blob — default to 64-bit x86_64 disassembly from offset 0
        return ("Raw Binary (x86_64)", 64, 0, 0UL, true);
    }
}
