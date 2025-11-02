using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RomInstaller.Core.Services;

/// <summary>
/// ConsoleDetector
/// ----------------
/// Performs lightweight console identification and title sanitization
/// based purely on a ROM file’s extension.
///
/// This is an intentionally *naive* detector used early in planning,
/// before any emulator-specific database or signature scan exists.
/// It never throws; it simply returns `"unknown"` if a mapping is not found.
///
/// Future roadmap:
///   • Integrate `filetypes.json` so new consoles and extensions are data-driven
///   • Add multi-extension detection (e.g. `.cue`+`.bin`, `.chd`, `.zip`+inner file)
///   • Add header/signature sniffing (e.g., NES magic bytes)
///   • Allow user override via CLI flags (`--console`)
/// </summary>
public static class ConsoleDetector
{
    /// <summary>
    /// Static map of known extensions → canonical console IDs.
    /// Case-insensitive dictionary.
    /// </summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".iso"] = "ps2",
        [".bin"] = "ps1",
        [".cue"] = "ps1",
        [".sfc"] = "snes",
        [".smc"] = "snes",
        [".gba"] = "gba",
        [".nds"] = "nds",
        [".rvz"] = "gc",
        [".wbfs"] = "wii",
        [".cso"] = "psp",

        // Ambiguous formats (archives or mixed contents)
        [".zip"] = "unknown",
        [".7z"] = "unknown"
    };

    /// <summary>
    /// Attempts to infer a console ID from a file extension.
    /// Returns `"unknown"` for unrecognized or ambiguous formats.
    /// Never throws.
    /// </summary>
    public static string DetectByExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "unknown";

        var ext = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(ext))
            return "unknown";

        return Map.TryGetValue(ext, out var console)
            ? console
            : "unknown";
    }

    /// <summary>
    /// Produces a clean, folder-safe title for display and storage.
    /// Example:
    ///     Input : "Super.Mario.World (USA) [v1.1].sfc"
    ///     Output: "Super.Mario.World (USA) [v1.1]"
    ///
    /// Removes unsafe filesystem characters but keeps parentheses/brackets/version info.
    /// </summary>
    public static string SanitizeTitle(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "Untitled";

        // Strip extension and illegal characters
        var name = Path.GetFileNameWithoutExtension(filename);

        // Remove non-word characters except safe punctuation: - _ ( ) [ ] .
        name = Regex.Replace(name, @"[^\w\-\s\(\)\[\]\.]", "");

        // Collapse multiple spaces and trim
        name = Regex.Replace(name, @"\s+", " ").Trim();

        return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
    }
}
