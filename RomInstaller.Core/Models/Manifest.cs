using System;
using System.Collections.Generic;

namespace RomInstaller.Core.Models;

/// <summary>
/// Manifest
/// --------
/// Persistent record of all ROMs installed by RomInstaller, stored at:
/// <c>%APPDATA%\RomInstaller\manifest.json</c>.
///
/// Each <see cref="GameEntry"/> represents a single installed title with
/// metadata for launch, shortcut, and tracking.
///
/// Notes:
/// • This file is managed automatically by the installer and launcher.
/// • It is safe for users to back up or migrate.
/// </summary>
public record Manifest
{
    /// <summary>Schema version (reserved for migration).</summary>
    public int Schema { get; init; } = 1;

    /// <summary>List of installed games and their associated metadata.</summary>
    public List<GameEntry> Games { get; init; } = [];
}

/// <summary>
/// Represents a single installed ROM entry within the manifest.
/// </summary>
public record GameEntry
{
    /// <summary>
    /// Unique manifest identifier for this installation.  
    /// Auto-generated as a 32-character lowercase GUID (no hyphens).
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing game title (e.g., “Final Fantasy IX”).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Console identifier (e.g., "ps1", "gba", "snes").</summary>
    public string Console { get; init; } = string.Empty;

    /// <summary>Emulator ID used to launch this game (e.g., "duckstation").</summary>
    public string EmulatorId { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path to the ROM or cue/m3u file used as the primary entry point.
    /// </summary>
    public string RomPath { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path to the containing game folder (useful for save/config cleanup).
    /// </summary>
    public string GameFolder { get; init; } = string.Empty;

    /// <summary>
    /// Optional path to a generated desktop shortcut (.lnk), if created.
    /// </summary>
    public string? ShortcutPath { get; init; }

    /// <summary>UTC timestamp when this game was installed.</summary>
    public DateTime InstalledAt { get; init; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when this game was last launched (optional).</summary>
    public DateTime? LastPlayedAt { get; init; }

    /// <summary>
    /// Identifier or filename of a selected content “pack” (e.g., custom settings or assets).
    /// Future expansion field; optional.
    /// </summary>
    public string? SelectedPack { get; init; }
}
