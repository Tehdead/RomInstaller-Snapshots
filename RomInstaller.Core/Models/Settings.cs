using System.Collections.Generic;
using RomInstaller.Core.Services;

namespace RomInstaller.Core.Models;

/// <summary>
/// User settings persisted to %APPDATA%\RomInstaller\settings.json.
///
/// Notes:
/// - Lean, JSON-friendly record with init-only properties.
/// - Safe defaults ensure `Json.Load<Settings>() ?? new Settings()` always yields usable data.
/// - Validation/migration handled externally (e.g., CLI `validate-config`).
/// </summary>
public record Settings
{
    /// <summary>Schema version for settings.json (reserved for future migrations).</summary>
    public int Schema { get; init; } = 1;

    /// <summary>
    /// Root folder for emulation content (ROMs/BIOS/saves).  
    /// Defaults to %USERPROFILE%\Emulation via CorePaths.DefaultEmulationRoot.
    /// </summary>
    public string EmulationRoot { get; init; } = CorePaths.DefaultEmulationRoot;

    /// <summary>
    /// File extensions (with leading dot) that the Explorer “Install ROM” verb should be
    /// registered for on this system. Only affects context-menu registration — the
    /// authoritative extension→console mapping lives in filetypes.json.
    /// </summary>
    public string[] RegisteredExtensions { get; init; } =
        [".iso", ".bin", ".cue", ".sfc", ".smc", ".gba", ".nds", ".zip", ".7z"];

    /// <summary>
    /// Preferred emulator id per console id (e.g., { "ps2": "pcsx2" }).
    /// Values must match an emulator <c>id</c> in emulators.json.
    /// </summary>
    public Dictionary<string, string>? DefaultEmulatorPerConsole { get; init; }

    /// <summary>
    /// Determines whether the CLI automatically creates a desktop shortcut
    /// after each successful install.
    /// 
    /// Default: true (auto-create)
    /// 
    /// Can be overridden per-command using:
    ///   --shortcut       (force ON)
    ///   --no-shortcut    (force OFF)
    /// </summary>
    public bool AutoCreateShortcut { get; init; } = true;
    
    /// <summary>
    /// Determines where the CLI should store Shortcut .ink Files
    /// </summary>
    public string? ShortcutsRoot { get; init; }
}
