using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualBasic.FileIO;
// ^ No NuGet needed. This is in the BCL; just reference the namespace.

namespace RomInstaller.Core.Services;

/// <summary>
/// Centralized, failure-safe delete operations with support for:
///   • Recycle Bin (normal delete)
///   • Permanent delete (wipes immediately)
///
/// Design:
///   • Never throws outwards — returns bool and emits logs
///   • Works for single files or directories (recursive)
///   • Logs exact reasons for failures
/// </summary>
public static class FileDeleteService
{
    public enum DeletionMode { RecycleBin, Permanent }

    /// <summary>Delete a single file according to the chosen mode.</summary>
    public static bool TryDeleteFile(string path, DeletionMode mode, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            { error = $"File not found: {path}"; return false; }

            if (mode == DeletionMode.RecycleBin)
            {
                // Sends to Recycle Bin without UI prompts, no error dialogs
                FileSystem.DeleteFile(path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
            else
            {
                // Permanent delete
                MakeWritableIfReadOnly(path);
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception("TryDeleteFile", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Delete a directory (recursive) according to the chosen mode.</summary>
    public static bool TryDeleteDirectory(string path, DeletionMode mode, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            { error = $"Folder not found: {path}"; return false; }

            if (mode == DeletionMode.RecycleBin)
            {
                // Sends to Recycle Bin (recursive) without confirmations
                FileSystem.DeleteDirectory(path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
            else
            {
                // Permanent delete (recursive)
                MakeWritableIfReadOnly(path);
                Directory.Delete(path, recursive: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception("TryDeleteDirectory", ex);
            error = ex.Message;
            return false;
        }
    }

    // ---------- internal helpers ----------

    private static void MakeWritableIfReadOnly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var a = File.GetAttributes(path);
                if (a.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories))
                {
                    var a = File.GetAttributes(file);
                    if (a.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(file, a & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception ex)
        {
            // Not fatal; deletion may still succeed. Log for diagnostics.
            Logger.Exception("MakeWritableIfReadOnly", ex);
        }
    }
}
