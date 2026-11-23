using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VoidCode.Services;

namespace VoidCode.Tests;

/// <summary>
/// Answers every <see cref="IFileSystem"/> call from fields a test sets, and records the
/// Mutations in order so a test can assert sequencing (checkpoint before write) as well as outcomes.
/// Shared by the file-system and session domain tests - a second copy would drift.
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    public bool Exists { get; set; }
    public string Text { get; set; } = "";
    public bool ImageFile { get; set; }
    public bool BinaryFile { get; set; }
    public string DataUri { get; set; } = "data:image/png;base64,AA==";
    public long ImageBytes { get; set; }
    public FileNode Tree { get; set; } = new() { Name = "tree", IsDirectory = true };
    public FileCheckpoint? Checkpoint { get; set; }
    public string? RestoredContent { get; set; }

    public Exception? WriteThrows { get; set; }
    public Exception? RenameThrows { get; set; }
    public Exception? ReadThrows { get; set; }

    private readonly List<string> _calls = new();

    public IReadOnlyList<string> Calls { get { lock (_calls) { return _calls.ToArray(); } } }

    public int Checkpoints { get { lock (_calls) { return _calls.Count(c => c.StartsWith("checkpoint:", StringComparison.Ordinal)); } } }

    private void Record(string call)
    {
        lock (_calls) { _calls.Add(call); }
    }

    private static string Name(string path) => Path.GetFileName(path);

    public bool FileExists(string path) => Exists;

    public string ReadAllText(string path) => RestoredContent ?? Text;

    public string ReadFile(string path) => ReadThrows != null ? throw ReadThrows : Text;

    public TextFileMetadata GetTextFileMetadata(string path, string content) => new();

    public bool IsImageFile(string path) => ImageFile;

    public bool IsBinaryFile(string path) => BinaryFile;

    public string GetImageDataUri(string path, out long sizeBytes)
    {
        sizeBytes = ImageBytes;
        return DataUri;
    }

    public string InspectBinary(string path) => "binary report";

    public FileNode GetDirectoryTree(string rootPath) => Tree;

    public DirectoryChildrenResult GetDirectoryChildrenPage(string rootPath, string directoryPath, int offset) => new() { Offset = offset };

    public List<object> GetAllFilesFlat(string rootPath, CancellationToken cancellationToken = default) => new();

    public FileCheckpoint CreateCheckpoint(string fullPath, string label)
    {
        Record($"checkpoint:{label}");
        return Checkpoint ?? new FileCheckpoint { Id = "cp_1", FilePath = fullPath, PreviousContent = Text };
    }

    public FileCheckpoint? GetCheckpoint(string checkpointId)
    {
        Record($"get-checkpoint:{checkpointId}");
        return Checkpoint;
    }

    public bool RestoreCheckpoint(string checkpointId, out string restoredPath)
    {
        Record($"restore-checkpoint:{checkpointId}");
        restoredPath = Checkpoint?.FilePath ?? "";
        return Checkpoint != null;
    }

    public void WriteFile(string path, string content)
    {
        if (WriteThrows != null) throw WriteThrows;
        Record($"write:{Name(path)}:{content}");
    }

    public void CreateFile(string path) => Record($"create:{Name(path)}");

    public void CreateDirectory(string path) => Record($"mkdir:{Name(path)}");

    public void Rename(string oldPath, string newName)
    {
        if (RenameThrows != null) throw RenameThrows;
        Record($"rename:{Name(oldPath)}:{newName}");
    }

    public void Delete(string path) => Record($"delete:{Name(path)}");
}