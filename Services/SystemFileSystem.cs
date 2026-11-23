using System.Collections.Generic;
using System.Threading;

namespace VoidCode.Services;

/// <summary>
/// The real <see cref="IFileSystem"/>: one delegation per member to <see cref="FileSystemService"/>, so
/// Production behaviour is unchanged and the service keeps its encoding detection, ignore rules and
/// Checkpoint bookkeeping. Nothing is re-implemented here on purpose.
/// </summary>
public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => System.IO.File.Exists(path);

    public string ReadAllText(string path) => System.IO.File.ReadAllText(path);

    public string ReadFile(string path) => FileSystemService.ReadFile(path);

    public TextFileMetadata GetTextFileMetadata(string path, string content) => FileSystemService.GetTextFileMetadata(path, content);

    public bool IsImageFile(string path) => FileSystemService.IsImageFile(path);

    public bool IsBinaryFile(string path) => FileSystemService.IsBinaryFile(path);

    public string GetImageDataUri(string path, out long sizeBytes) => FileSystemService.GetImageDataUri(path, out sizeBytes);

    public string InspectBinary(string path) => FileSystemService.InspectBinary(path);

    public FileNode GetDirectoryTree(string rootPath) => FileSystemService.GetDirectoryTree(rootPath);

    public DirectoryChildrenResult GetDirectoryChildrenPage(string rootPath, string directoryPath, int offset) =>
        FileSystemService.GetDirectoryChildrenPage(rootPath, directoryPath, offset);

    public List<object> GetAllFilesFlat(string rootPath, CancellationToken cancellationToken = default) =>
        FileSystemService.GetAllFilesFlat(rootPath, cancellationToken: cancellationToken);

    public FileCheckpoint CreateCheckpoint(string fullPath, string label) => FileSystemService.CreateCheckpoint(fullPath, label);

    public FileCheckpoint? GetCheckpoint(string checkpointId) => FileSystemService.GetCheckpoint(checkpointId);

    public bool RestoreCheckpoint(string checkpointId, out string restoredPath) =>
        FileSystemService.RestoreCheckpoint(checkpointId, out restoredPath);

    public void WriteFile(string path, string content) => FileSystemService.WriteFile(path, content);

    public void CreateFile(string path) => FileSystemService.CreateFile(path);

    public void CreateDirectory(string path) => FileSystemService.CreateDirectory(path);

    public void Rename(string oldPath, string newName) => FileSystemService.Rename(oldPath, newName);

    public void Delete(string path) => FileSystemService.Delete(path);
}