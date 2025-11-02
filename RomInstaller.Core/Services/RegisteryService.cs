using Microsoft.Win32;
using System;
using System.IO;

namespace RomInstaller.Core.Services;

/// <summary>
/// Per-user registry writes for Explorer context-menu verbs under:
///   HKCU\Software\Classes\SystemFileAssociations\.ext\shell\Install ROM\command
///
/// Design:
///  • Never throw outward — return false + error string
///  • Write both the verb key (label/icon) and the command key (actual args)
///  • Keep the command simple and explicit:
///        "<appPath>" install "%1" --apply (--copy|--move) [--shortcut|--no-shortcut]
/// </summary>
public static class RegistryService
{
    /// <summary>
    /// Register "Install ROM" verb for a specific extension.
    ///
    /// Parameters:
    ///  • ext          : extension INCLUDING leading dot, e.g. ".iso"
    ///  • appPath      : full path to RomInstaller.CLI.exe
    ///  • copyMode     : true  => register with --copy  (default, safe)
    ///                   false => register with --move
    ///  • shortcutMode : null  => follow Settings.AutoCreateShortcut at runtime
    ///                   true  => force --shortcut
    ///                   false => force --no-shortcut
    ///
    /// Writes (example for .iso):
    ///  HKCU\Software\Classes\SystemFileAssociations\.iso\shell\Install ROM\(Default) = "Install ROM"
    ///                                                                Icon            = "<appPath>,0"           (optional)
    ///                                                                Position        = "Top"                   (optional)
    ///                                                              \command\(Default)= "\"<appPath>\" install \"%1\" --apply --copy"
    /// </summary>
    public static bool RegisterInstallVerbForExtension(
        string ext,
        string appPath,
        out string? error,
        bool copyMode = true,
        bool? shortcutMode = null)
    {
        error = null;

        // --------- Validate inputs ----------
        if (!TryNormalizeExt(ext, out var normalizedExt, out error)) return false;
        if (string.IsNullOrWhiteSpace(appPath))
        { error = "Application path is empty."; return false; }
        if (!File.Exists(appPath))
        { error = $"Application not found: {appPath}"; return false; }

        try
        {
            var verbKeyPath = $@"Software\Classes\SystemFileAssociations\{normalizedExt}\shell\Install ROM";
            var cmdKeyPath = $@"{verbKeyPath}\command";

            // Build the exact command line Explorer will invoke.
            var commandLine = BuildCommand(appPath, copyMode, shortcutMode);

            // Create/overwrite the verb key (label + optional icon/position)
            using (var verbKey = Registry.CurrentUser.CreateSubKey(verbKeyPath))
            {
                // Display label in the context menu.
                verbKey?.SetValue("", "Install ROM", RegistryValueKind.String);

                // Optional: place higher in the menu (supported by Explorer).
                verbKey?.SetValue("Position", "Top", RegistryValueKind.String);

                // Optional: show our app icon next to the verb.
                // Format: "<path>,index". Index 0 is fine for most EXEs.
                var iconValue = $"\"{appPath}\",0";
                verbKey?.SetValue("Icon", iconValue, RegistryValueKind.String);
            }

            // Write the concrete command
            using (var cmdKey = Registry.CurrentUser.CreateSubKey(cmdKeyPath))
            {
                cmdKey?.SetValue("", commandLine, RegistryValueKind.String);
            }

            return true;
        }
        catch (UnauthorizedAccessException uax)
        {
            error = $"Registry write failed for {normalizedExt}: access denied ({uax.Message}).";
            Logger.Exception("RegisterInstallVerbForExtension", uax);
            return false;
        }
        catch (Exception ex)
        {
            error = $"Registry write failed for {normalizedExt}: {ex.Message}";
            Logger.Exception("RegisterInstallVerbForExtension", ex);
            return false;
        }
    }

    /// <summary>
    /// Remove our verb key; non-fatal if the key does not exist.
    /// </summary>
    public static bool UnregisterInstallVerbForExtension(string ext, out string? error)
    {
        error = null;
        if (!TryNormalizeExt(ext, out var normalizedExt, out error)) return false;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\SystemFileAssociations\{normalizedExt}\shell\Install ROM",
                throwOnMissingSubKey: false);
            return true;
        }
        catch (UnauthorizedAccessException uax)
        {
            error = $"Registry delete failed for {normalizedExt}: access denied ({uax.Message}).";
            Logger.Exception("UnregisterInstallVerbForExtension", uax);
            return false;
        }
        catch (Exception ex)
        {
            error = $"Registry delete failed for {normalizedExt}: {ex.Message}";
            Logger.Exception("UnregisterInstallVerbForExtension", ex);
            return false;
        }
    }

    // ---------------------------
    // Internals / helpers
    // ---------------------------

    /// <summary>Normalize and validate extension; require leading dot.</summary>
    private static bool TryNormalizeExt(string ext, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(ext))
        { error = "Extension is empty."; return false; }

        normalized = ext.Trim();
        if (!normalized.StartsWith(".")) normalized = "." + normalized;

        // A bare dot is invalid.
        if (normalized == ".")
        { error = $"Invalid extension '{ext}'."; return false; }

        return true;
    }

    /// <summary>
    /// Build the exact command line to register.
    /// We deliberately quote the app path and pass "%1" (the clicked file) quoted.
    /// </summary>
    private static string BuildCommand(string appPath, bool copyMode, bool? shortcutMode)
    {
        var moveOrCopy = copyMode ? "--copy" : "--move";
        var shortcutFlag = shortcutMode switch
        {
            true => " --shortcut",
            false => " --no-shortcut",
            _ => string.Empty
        };

        // IMPORTANT: keep "%1" intact (Explorer substitutes the file path).
        // Example:
        //   "<appPath>" install "%1" --apply --copy --shortcut
        return $"\"{appPath}\" install \"%1\" --apply {moveOrCopy}{shortcutFlag}";
    }

    /// <summary>
    /// Registers a "Uninstall ROM" verb for *.lnk shortcuts (ProgID: lnkfile),
    /// pointing to our CLI with the --shortcut "%1" contract.
    ///
    /// Key path:
    ///   HKCU\Software\Classes\lnkfile\shell\Uninstall ROM\command
    /// </summary>
    public static bool RegisterUninstallVerbForShortcuts(string appPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(appPath))
        { error = "Application path is empty."; return false; }

        try
        {
            var cmd = $"\"{appPath}\" uninstall --shortcut \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\lnkfile\shell\Uninstall ROM\command");
            key?.SetValue("", cmd);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Registry write failed for uninstall verb: {ex.Message}";
            Logger.Exception("RegisterUninstallVerbForShortcuts", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes the "Uninstall ROM" verb for *.lnk shortcuts (non-fatal if missing).
    /// </summary>
    public static bool UnregisterUninstallVerbForShortcuts(out string? error)
    {
        error = null;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\lnkfile\shell\Uninstall ROM", false);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Registry delete failed for uninstall verb: {ex.Message}";
            Logger.Exception("UnregisterUninstallVerbForShortcuts", ex);
            return false;
        }
    }
}