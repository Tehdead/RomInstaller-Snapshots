using System;
using System.Collections.Generic;

namespace RomInstaller.Core.Models;

/// <summary>
/// EmulatorCatalog
/// ----------------
/// Central registry of known emulators, persisted in <c>emulators.json</c>.
/// Defines how RomInstaller knows:
///  • Which consoles each emulator supports
///  • How to launch them (argument templates, paths)
///  • Whether BIOS files are required, etc.
///
/// Shipped via seed and user-editable for custom setups.
/// </summary>
public record EmulatorCatalog
{
    /// <summary>Schema version for emulator catalog (reserved for migrations).</summary>
    public int Schema { get; init; } = 1;

    /// <summary>List of all known emulators (e.g., PCSX2, DuckStation, RetroArch).</summary>
    public List<EmulatorSpec> Emulators { get; init; } = [];
}

/// <summary>
/// Defines a single emulator entry within the catalog.
/// </summary>
public record EmulatorSpec
{
    /// <summary>Stable identifier (e.g., <c>"duckstation"</c>, <c>"pcsx2"</c>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Friendly display name (e.g., “DuckStation”, “PCSX2”).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// List of console IDs this emulator supports  
    /// (e.g., <c>["ps1"]</c>, <c>["ps2"]</c>, <c>["gba","gbc"]</c>).
    /// </summary>
    public string[] Consoles { get; init; } = [];

    /// <summary>
    /// Launch argument template.  
    /// Tokens:
    ///  • <c>{ROM}</c> → full ROM path (auto-quoted)  
    ///  • <c>{EMULATOR}</c> → emulator exe path
    /// </summary>
    public string ArgTemplate { get; init; } = "\"{EMULATOR}\" \"{ROM}\"";

    /// <summary>
    /// Portable emulator executable path.  
    /// Supports <c>%EMULATION%</c> variable → <see cref="Settings.EmulationRoot"/>.
    /// </summary>
    public string? PortablePath { get; init; }

    /// <summary>Future: registry hint for system-installed emulators.</summary>
    public string? InstalledHint { get; init; }

    /// <summary>True if BIOS files are required.</summary>
    public bool BiosRequired { get; init; }

    /// <summary>Expected BIOS filenames (if <see cref="BiosRequired"/> is true).</summary>
    public string[]? BiosExpected { get; init; }
}
