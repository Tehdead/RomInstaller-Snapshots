using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using RomInstaller.Core.Models;

namespace RomInstaller.Core.Services;

/// <summary>
/// Planner
/// -------
/// Computes an InstallPlan from a source ROM path. **No disk writes** here.
/// Uses <see cref="FileTypesIndex"/> for console inference (by extension),
/// supports explicit overrides, and normalizes console IDs (psx→ps1, gc→gamecube).
/// </summary>
public class Planner
{
    private readonly Settings _settings;
    private readonly EmulatorCatalog _catalog;
    private readonly FileTypesIndex _ftIndex;

    public Planner(Settings settings, EmulatorCatalog catalog, FileTypesIndex ftIndex)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ftIndex = ftIndex ?? throw new ArgumentNullException(nameof(ftIndex));
    }

    /// <summary>
    /// Build a plan for a single source file.
    /// If detection is ambiguous/missing, the plan sets <see cref="InstallPlan.NeedsPrompt"/> and adds Notes.
    /// 
    /// Parameters:
    ///  • sourcePath: full path to the candidate ROM file
    ///  • consoleOverride: optional canonical/alias console id (e.g., "ps1", "psx", "gamecube", "gc")
    ///  • emulatorOverride: optional emulator id from catalog (e.g., "retroarch", "pcsx2")
    /// </summary>
    public InstallPlan PlanFromFile(string sourcePath, string? consoleOverride = null, string? emulatorOverride = null)
    {
        var notes = new List<string>();

        // ---------- Basic checks ----------
        if (string.IsNullOrWhiteSpace(sourcePath))
            return Fail("Source path is empty.");
        if (!File.Exists(sourcePath))
            return Fail($"Source file not found: {sourcePath}");

        // ---------- Console inference (override > filetypes) ----------
        string console;
        if (!string.IsNullOrWhiteSpace(consoleOverride))
        {
            // Respect override but normalize alias → canonical id
            console = NormalizeConsoleId(consoleOverride!);
        }
        else
        {
            var ext = FileTypesIndex.GetPathExtNoDot(sourcePath); // "cue", "bin", "rvz", etc.
            if (string.IsNullOrEmpty(ext) || !_ftIndex.AllExtensions.Contains(ext))
            {
                console = "unknown";
                notes.Add("Extension not recognized by filetypes.json; user selection required.");
            }
            else
            {
                if (_ftIndex.ExtToConsoles.TryGetValue(ext, out var consoles) && consoles.Count > 0)
                {
                    if (consoles.Count == 1)
                    {
                        console = NormalizeConsoleId(consoles[0]);
                    }
                    else
                    {
                        // Ambiguous: multiple possible consoles; don’t guess.
                        console = "unknown";
                        notes.Add($"Extension '{ext}' matches multiple consoles: {string.Join(", ", consoles)}.");
                    }
                }
                else
                {
                    console = "unknown";
                    notes.Add("Extension recognized but not mapped to any console.");
                }
            }
        }

        // ---------- Title ----------
        var title = ConsoleDetector.SanitizeTitle(Path.GetFileName(sourcePath));

        // ---------- Emulator selection (override > settings default > catalog fallback) ----------
        string emuId;
        if (!string.IsNullOrWhiteSpace(emulatorOverride))
        {
            emuId = emulatorOverride!;
            // Validate the override actually exists
            if (!_catalog.Emulators.Any(e => e.Id.Equals(emuId, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"Emulator override '{emuId}' not found in catalog.");
                emuId = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(console) && console != "unknown")
            {
                // Validate the emulator claims support for the (normalized) console
                if (!CatalogSupportsConsole(emuId, console))
                {
                    notes.Add($"Emulator '{emuId}' does not declare support for console '{console}'.");
                }
            }
        }
        else
        {
            emuId = ResolveEmulator(console);
            if (string.IsNullOrEmpty(emuId))
                notes.Add($"No emulator preference found for console '{console}'.");
        }

        // ---------- Destination paths ----------
        var root = string.IsNullOrWhiteSpace(_settings.EmulationRoot)
            ? CorePaths.DefaultEmulationRoot
            : _settings.EmulationRoot;

        var consoleFolder = (string.IsNullOrWhiteSpace(console) || console == "unknown")
            ? "UNKNOWN"
            : console.ToUpperInvariant();

        var gameFolder = Path.Combine(root, consoleFolder, "ROMs", title);
        var destRom = Path.Combine(gameFolder, Path.GetFileName(sourcePath));

        // ---------- Prompt requirement ----------
        var needsPrompt = console == "unknown" || string.IsNullOrEmpty(emuId);

        return new InstallPlan
        {
            SourcePath = sourcePath,
            Console = console,
            Title = title,
            EmulatorId = emuId,
            DestinationGameFolder = gameFolder,
            DestinationRomPath = destRom,
            NeedsPrompt = needsPrompt,
            Notes = notes.ToArray()
        };

        // Local helper to return a "soft fail" plan (no exceptions from Planner)
        InstallPlan Fail(string msg)
        {
            Logger.Warn($"PlanFromFile: {msg}");
            return new InstallPlan
            {
                SourcePath = sourcePath ?? "",
                Console = "unknown",
                Title = "Untitled",
                EmulatorId = "",
                DestinationGameFolder = "",
                DestinationRomPath = "",
                NeedsPrompt = true,
                Notes = new[] { msg }
            };
        }
    }

    /// <summary>
    /// Resolve emulator id for a given console:
    ///  1) user default in settings.defaultEmulatorPerConsole
    ///  2) first emulator in catalog that supports the console (after normalization)
    /// Returns empty string if console is unknown or nothing matches.
    /// </summary>
    private string ResolveEmulator(string console)
    {
        if (string.IsNullOrWhiteSpace(console) || console == "unknown")
            return "";

        var canon = NormalizeConsoleId(console);

        // 1) user default
        if (_settings.DefaultEmulatorPerConsole != null &&
            _settings.DefaultEmulatorPerConsole.TryGetValue(canon, out var preferred) &&
            !string.IsNullOrWhiteSpace(preferred) &&
            CatalogSupportsConsole(preferred, canon))
        {
            return preferred;
        }

        // 2) first emulator in catalog that supports this console (normalized)
        var first = _catalog.Emulators.FirstOrDefault(e =>
            e.Consoles.Any(c => NormalizeConsoleId(c).Equals(canon, StringComparison.OrdinalIgnoreCase)));
        return first?.Id ?? "";
    }

    /// <summary>
    /// Returns true if the emulator with <paramref name="emulatorId"/> declares support
    /// for the (normalized) <paramref name="console"/>.
    /// </summary>
    private bool CatalogSupportsConsole(string emulatorId, string console)
    {
        var canon = NormalizeConsoleId(console);
        var emu = _catalog.Emulators.FirstOrDefault(e =>
            e.Id.Equals(emulatorId, StringComparison.OrdinalIgnoreCase));
        if (emu is null) return false;
        return emu.Consoles.Any(c => NormalizeConsoleId(c).Equals(canon, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Canonicalize console IDs so catalog/settings/filetypes can use common aliases.
    /// Examples:
    ///   "psx" / "playstation" / "psone" → "ps1"
    ///   "gc" / "gcn" / "game cube"      → "gamecube"
    ///   "sfc"                           → "snes"
    /// </summary>
    private static string NormalizeConsoleId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "unknown";
        return id.Trim().ToLowerInvariant() switch
        {
            "psx" or "playstation" or "playstation1" or "psone" => "ps1",
            "gc" or "gcn" or "game cube" => "gamecube",
            "sfc" => "snes",
            _ => id.Trim().ToLowerInvariant()
        };
    }
}
