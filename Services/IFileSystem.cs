namespace VoidCode.Services;

/// <summary>
/// (second seam): the filesystem work the bridge handlers perform, as an interface. It is
/// Deliberately narrow - exactly the calls <see cref="VoidCode.Bridge.Handlers.FileSystemHandlers"/>
/// Makes - rather than a wrapper over all of <see cref="FileSystemService"/>, because the point is to
/// Make the *failure* paths testable: a save that hits a locked file, a rename that collides, a tree
/// That cannot be read. A fake can throw; a real disk cannot be made to, reliably.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    /// <summary>Raw text read, used where the bytes matter (comparing before a checkpoint).</summary>
    string ReadAllText(string path);

    string ReadFile(string path);

    TextFileMetadata GetTextFileMetadata(string path, string content);

    bool IsImageFile(string path);

    bool IsBinaryFile(string path);

    string GetImageDataUri(string path, out long sizeBytes);

    string InspectBinary(string path);

    FileNode GetDirectoryTree(string rootPath);

    DirectoryChildrenResult GetDirectoryChildrenPage(string rootPath, string directoryPath, int offset);

    List<object> GetAllFilesFlat(string rootPath, CancellationToken cancellationToken = default);

    FileCheckpoint CreateCheckpoint(string fullPath, string label);

    FileCheckpoint? GetCheckpoint(string checkpointId);

    bool RestoreCheckpoint(string checkpointId, out string restoredPath);

    void WriteFile(string path, string content);

    void CreateFile(string path);

    void CreateDirectory(string path);

    void Rename(string oldPath, string newName);

    void Delete(string path);
}