using System;
using System.IO;
using System.Linq;
using RomInstaller.Core.Models;

namespace RomInstaller.Core.Services;

/// <summary>
/// Removes a manifest entry and (optionally) deletes its ROM file or entire game folder.
/// Uses FileDeleteService to choose Recycle Bin vs permanent behavior.
/// </summary>
public sealed class Uninstaller
{
    public enum DeleteTarget
    {
        None,       // Keep files; just remove manifest entry (and optionally shortcut)
        RomFile,    // Delete only the primary ROM file
        GameFolder  // Delete the whole installed game folder (recursive)
    }

    /// <summary>
    /// Uninstall by manifest id.
    /// - deleteTarget: what to delete (none/rom/folder)
    /// - mode: RecycleBin (default) or Permanent
    /// - keepShortcut: true to keep .lnk; false to remove if present
    ///
    /// Returns a human-friendly summary string.
    /// Throws RomInstallerException on expected/usage errors.
    /// </summary>
    public string UninstallByKey(string manifestId, DeleteTarget deleteTarget,
                                 FileDeleteService.DeletionMode mode,
                                 bool keepShortcut)
    {
        if (string.IsNullOrWhiteSpace(manifestId))
            throw new RomInstallerException("Missing manifest id.");

        var manifest = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();
        var list = manifest.Games ?? [];
        var game = list.FirstOrDefault(g => g.Id == manifestId);
        if (game is null)
            throw new RomInstallerException($"Game not found: {manifestId}");

        // 1) Delete files as requested
        string? deleteErr = null;
        bool deleted = false;

        if (deleteTarget == DeleteTarget.RomFile && !string.IsNullOrWhiteSpace(game.RomPath))
        {
            deleted = FileDeleteService.TryDeleteFile(game.RomPath, mode, out deleteErr);
        }
        else if (deleteTarget == DeleteTarget.GameFolder && !string.IsNullOrWhiteSpace(game.GameFolder))
        {
            deleted = FileDeleteService.TryDeleteDirectory(game.GameFolder, mode, out deleteErr);
        }

        if (!string.IsNullOrEmpty(deleteErr))
            Logger.Warn($"Uninstall: file deletion error: {deleteErr}");

        // 2) Optionally remove shortcut
        if (!keepShortcut && !string.IsNullOrWhiteSpace(game.ShortcutPath))
        {
            try
            {
                if (File.Exists(game.ShortcutPath))
                {
                    // Use recycle vs permanent consistent with mode
                    if (mode == FileDeleteService.DeletionMode.RecycleBin)
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            game.ShortcutPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        File.Delete(game.ShortcutPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception("Uninstall.DeleteShortcut", ex);
                // Non-fatal
            }
        }

        // 3) Remove manifest entry
        var updated = list.Where(g => g.Id != manifestId).ToList();
        Json.Save(CorePaths.ManifestPath, manifest with { Games = updated });

        // 4) Human-friendly summary
        var what = deleteTarget switch
        {
            DeleteTarget.None => "kept files",
            DeleteTarget.RomFile => deleted ? "ROM sent to " + (mode == FileDeleteService.DeletionMode.RecycleBin ? "Recycle Bin" : "trash") : "ROM kept",
            DeleteTarget.GameFolder => deleted ? "game folder sent to " + (mode == FileDeleteService.DeletionMode.RecycleBin ? "Recycle Bin" : "trash") : "game folder kept",
            _ => "done"
        };

        var shortcutNote = keepShortcut ? " (shortcut kept)" : " (shortcut removed if present)";
        return $"Uninstalled '{game.Title}' — {what}{shortcutNote}.";
    }
}
