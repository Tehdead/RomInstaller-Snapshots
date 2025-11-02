using System;
using System.IO;

namespace RomInstaller.Core.Services;

/// <summary>
/// CorePaths
/// ----------
/// Central registry for all well-known filesystem locations used by the
/// RomInstaller suite (CLI, Launcher, Core, etc.).
///
/// 💡 Design goals:
///  • Avoid hard-coded "stringly-typed" paths scattered throughout code
///  • Define *logical ownership* of every folder and file in one place
///  • Keep I/O side effects isolated — only `Ensure*` / `TryEnsure*` write to disk
///
/// Safe to call from anywhere; uses SpecialFolder APIs for portability.
/// Examples:
///   CorePaths.SettingsPath          → %AppData%\RomInstaller\settings.json
///   CorePaths.DefaultEmulationRoot  → C:\Users\<User>\Emulation
/// </summary>
public static class CorePaths
{
    /// <summary>
    /// Root folder under AppData where all RomInstaller configuration,
    /// manifests, and logs are stored.
    /// Example: C:\Users\<User>\AppData\Roaming\RomInstaller
    /// </summary>
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RomInstaller");

    /// <summary>Subfolder for rolling log files.</summary>
    public static string LogsDir => Path.Combine(AppDataRoot, "logs");

    /// <summary>Temporary folder for staging copies during install operations.</summary>
    public static string StagingDir => Path.Combine(AppDataRoot, "staging");

    /// <summary>Persistent manifest of installed games and metadata.</summary>
    public static string ManifestPath => Path.Combine(AppDataRoot, "manifest.json");

    /// <summary>User-configurable global settings (root path, emulator prefs, etc.).</summary>
    public static string SettingsPath => Path.Combine(AppDataRoot, "settings.json");

    /// <summary>List of available emulator definitions (from emulators.json).</summary>
    public static string EmulatorCatalogPath => Path.Combine(AppDataRoot, "emulators.json");

    /// <summary>List of recognized ROM extensions and console mappings (from filetypes.json).</summary>
    public static string FiletypesPath => Path.Combine(AppDataRoot, "filetypes.json");

    /// <summary>
    /// Default base path for all emulation content (ROMs, BIOS, saves, etc.).
    /// Example: C:\Users\<User>\Emulation
    ///
    /// Used when no explicit root is defined in settings.json.
    /// </summary>
    public static string DefaultEmulationRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Emulation");

    /// <summary>
    /// Ensures that all critical RomInstaller application folders exist.
    /// Creates them if missing. Idempotent.
    /// </summary>
    public static void EnsureAppFolders()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(StagingDir);
    }

    /// <summary>
    /// Attempts to create a directory safely.  
    /// Returns <c>true</c> on success, or <c>false</c> and an error string on failure.
    /// </summary>
    public static bool TryEnsureDirectory(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to create directory '{path}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Ensures the parent directory of a file path exists (useful before atomic saves).
    /// Returns <c>true</c> on success; otherwise <c>false</c> and an error message.
    /// </summary>
    public static bool TryEnsureParentDirectory(string filePath, out string? error)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to create parent directory for '{filePath}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Centralized helper to pick the emulation root:
    /// returns <see cref="DefaultEmulationRoot"/> when <paramref name="configured"/> is null/whitespace.
    /// </summary>
    public static string ResolveEmulationRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultEmulationRoot : configured;
}
