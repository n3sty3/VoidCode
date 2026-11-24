using System;
using System.IO;
using Microsoft.Win32;
using VoidCode.Services;

namespace VoidCode.Native;

/// <summary>
/// Registers Windows WPF native file/folder pickers into <see cref="PlatformServices"/>.
/// </summary>
internal static class WindowsPlatformServices
{
    internal static void Register()
    {
        PlatformServices.RegisterDialogs(PromptOpenFolder, PromptOpenFiles);
    }

    private static string? PromptOpenFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Project Folder",
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string[] PromptOpenFiles(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Files to Attach",
            Multiselect = true,
            Filter = "All Files (*.*)|*.*"
        };

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }
}
