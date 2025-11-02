using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;
// Comes with .NET; no extra package needed.

namespace RomInstaller.Core.Services;

/// <summary>
/// DeletionService
/// ----------------
/// Centralized "delete" helpers with two modes:
///  • Recycle Bin (default): user-friendly, recoverable
///  • Permanent: immediate removal
///
/// All methods are best-effort and log (never throw outward).
/// </summary>
public static class DeletionService
{
    /// <summary>
    /// Delete a file. If <paramref name="permanent"/> is false, it is sent to the Recycle Bin.
    /// </summary>
    public static void DeleteFile(string path, bool permanent)
    {
        try
        {
            if (!File.Exists(path)) return;

            if (permanent)
            {
                File.Delete(path);
            }
            else
            {
                // Uses Windows Shell to move to Recycle Bin
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex)
        {
            Logger.Exception($"DeletionService.DeleteFile({path})", ex);
        }
    }

    /// <summary>
    /// Delete a directory (recursive). If <paramref name="permanent"/> is false,
    /// the folder is sent to the Recycle Bin.
    /// </summary>
    public static void DeleteDirectory(string path, bool permanent)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            if (permanent)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                FileSystem.DeleteDirectory(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex)
        {
            Logger.Exception($"DeletionService.DeleteDirectory({path})", ex);
        }
    }
}
