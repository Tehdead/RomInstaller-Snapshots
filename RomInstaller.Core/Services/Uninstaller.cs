using System;
using System.IO;
using System.Linq;
using RomInstaller.Core.Models;

namespace RomInstaller.Core.Services;

/// <summary>
/// Authoritative uninstall flow. Requested game deletion must succeed before
/// shortcuts or the manifest entry are removed. Shortcut deletion is best-effort.
/// </summary>
public sealed class Uninstaller
{
    public enum DeleteTarget
    {
        None,
        RomFile,
        GameFolder,
        Auto // Prefer an existing game folder, otherwise delete the primary ROM.
    }

    internal delegate bool DeleteOperation(string path, FileDeleteService.DeletionMode mode, out string? error);
    private readonly string manifestPath;
    private readonly DeleteOperation deleteFile;
    private readonly DeleteOperation deleteDirectory;

    public Uninstaller() : this(CorePaths.ManifestPath, FileDeleteService.TryDeleteFile,
                               FileDeleteService.TryDeleteDirectory) { }

    internal Uninstaller(string manifestPath, DeleteOperation deleteFile, DeleteOperation deleteDirectory)
    {
        this.manifestPath = manifestPath;
        this.deleteFile = deleteFile;
        this.deleteDirectory = deleteDirectory;
    }

    /// <summary>
    /// Uninstall by manifest id. Throws RomInstallerException on expected errors,
    /// including failed game-file deletion; the manifest is retained for retry.
    /// shortcutOnly keeps both game files and the manifest entry.
    /// </summary>
    public string UninstallByKey(string manifestId, DeleteTarget deleteTarget,
                                 FileDeleteService.DeletionMode mode, bool keepShortcut,
                                 bool shortcutOnly = false, string? shortcutPath = null)
    {
        if (string.IsNullOrWhiteSpace(manifestId))
            throw new RomInstallerException("Missing manifest id.");

        var manifest = Json.Load<Manifest>(manifestPath) ?? new Manifest();
        var list = manifest.Games ?? [];
        var game = list.FirstOrDefault(g => string.Equals(g.Id, manifestId, StringComparison.OrdinalIgnoreCase));
        if (game is null)
            throw new RomInstallerException($"Game not found: {manifestId}");

        if (shortcutOnly) deleteTarget = DeleteTarget.None;
        if (deleteTarget == DeleteTarget.Auto)
        {
            deleteTarget = !string.IsNullOrWhiteSpace(game.GameFolder) && Directory.Exists(game.GameFolder)
                ? DeleteTarget.GameFolder
                : !string.IsNullOrWhiteSpace(game.RomPath) ? DeleteTarget.RomFile : DeleteTarget.None;
        }

        string? deleteErr = null;
        bool deleted = deleteTarget switch
        {
            DeleteTarget.None => true,
            DeleteTarget.RomFile => deleteFile(game.RomPath, mode, out deleteErr),
            DeleteTarget.GameFolder => deleteDirectory(game.GameFolder, mode, out deleteErr),
            _ => throw new RomInstallerException("Invalid uninstall deletion target.")
        };
        if (!deleted)
        {
            var message = $"Uninstall failed: {deleteErr ?? "Game-file deletion failed."} Manifest entry retained; retry uninstall after resolving the deletion error.";
            Logger.Warn(message);
            throw new RomInstallerException(message);
        }

        if (deleteTarget == DeleteTarget.RomFile)
        {
            // Preserve the CLI's best-effort cleanup of an empty ROM parent.
            var parent = Path.GetDirectoryName(game.RomPath);
            try
            {
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)
                    && !Directory.EnumerateFileSystemEntries(parent).Any())
                    deleteDirectory(parent, mode, out _);
            }
            catch (Exception ex) { Logger.Exception("Uninstall.EmptyParent", ex); }
        }

        if (!keepShortcut)
        {
            foreach (var path in new[] { shortcutPath, game.ShortcutPath }
                         .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path) && !deleteFile(path!, mode, out var error))
                    Logger.Warn($"Uninstall: shortcut deletion error: {error}");
            }
        }

        if (shortcutOnly) return "Shortcut removed.";

        Json.Save(manifestPath, manifest with
        {
            Games = list.Where(g => !string.Equals(g.Id, game.Id, StringComparison.OrdinalIgnoreCase)).ToList()
        });

        var what = deleteTarget switch
        {
            DeleteTarget.None => "kept files",
            DeleteTarget.RomFile => mode == FileDeleteService.DeletionMode.RecycleBin ? "ROM sent to Recycle Bin" : "ROM permanently deleted",
            _ => mode == FileDeleteService.DeletionMode.RecycleBin ? "game folder sent to Recycle Bin" : "game folder permanently deleted"
        };
        return $"Uninstalled '{game.Title}' — {what}" +
               (keepShortcut ? " (shortcut kept)." : " (shortcut removal attempted).");
    }
}
