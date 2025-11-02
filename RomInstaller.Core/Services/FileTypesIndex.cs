using System;
using System.Collections.Generic;
using System.Linq;
using RomInstaller.Core.Models;

namespace RomInstaller.Core.Services;

/// <summary>
/// FileTypesIndex
/// ---------------
/// Precomputes and normalizes all lookup structures derived from <see cref="FileTypes"/>.
/// This makes runtime console detection very fast and consistent.
///
/// 🧩 Responsibilities:
///   • Build <see cref="AllExtensions"/>: all known extensions (without '.')
///   • Build <see cref="ExtToConsoles"/>: reverse mapping of extension → consoles supporting it
///   • Build <see cref="GlobalRank"/> and <see cref="PerConsoleRank"/>: extension preference order
///   • Merge <see cref="FileTypes.MultiMap"/> entries for explicit multi-console overlaps
///
/// ⚙️ Behavior:
///   • Case-insensitive comparison across all collections
///   • Defensive against duplicates and missing entries
///   • Safe even if <c>FileTypes.MultiMap</c> is null or incomplete
///
/// Example:
///   If "bin" appears in both "ps1" and "sega-cd", or MultiMap defines it,
///   ExtToConsoles["bin"] = [ "ps1", "sega-cd" ].
/// </summary>
public sealed class FileTypesIndex
{
    /// <summary>All known extensions (no dot, case-insensitive).</summary>
    public HashSet<string> AllExtensions { get; }

    /// <summary>Reverse map: extension → consoles that support it.</summary>
    public Dictionary<string, List<string>> ExtToConsoles { get; }

    /// <summary>Global priority rank of extensions (lower index = higher priority).</summary>
    public Dictionary<string, int> GlobalRank { get; }

    /// <summary>Per-console ranking of extensions (lower = preferred).</summary>
    public Dictionary<string, Dictionary<string, int>> PerConsoleRank { get; }

    public FileTypesIndex(FileTypes ft)
    {
        if (ft is null)
            throw new ArgumentNullException(nameof(ft));

        // ---------- Initialize base containers ----------
        AllExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtToConsoles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        GlobalRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        PerConsoleRank = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // ---------- 1. Build global ranking ----------
        // The GlobalPriority list defines overall importance of file types.
        for (int i = 0; i < ft.GlobalPriority.Count; i++)
        {
            var ext = NormalizeExt(ft.GlobalPriority[i]);
            if (string.IsNullOrEmpty(ext)) continue;

            // Lower index = higher priority
            if (!GlobalRank.ContainsKey(ext))
                GlobalRank[ext] = i;

            AllExtensions.Add(ext);
        }

        // ---------- 2. Build per-console maps ----------
        // Fill both the PerConsoleRank and the ExtToConsoles reverse lookup.
        foreach (var kvp in ft.PerConsole)
        {
            var console = kvp.Key;
            var list = kvp.Value ?? new List<string>();

            var rankMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                var ext = NormalizeExt(list[i]);
                if (string.IsNullOrEmpty(ext)) continue;

                rankMap[ext] = i;
                AllExtensions.Add(ext);

                if (!ExtToConsoles.TryGetValue(ext, out var consoles))
                {
                    consoles = new List<string>();
                    ExtToConsoles[ext] = consoles;
                }

                if (!consoles.Contains(console, StringComparer.OrdinalIgnoreCase))
                    consoles.Add(console);
            }

            PerConsoleRank[console] = rankMap;
        }

        // ---------- 3. Merge MultiMap (explicit cross-console overlaps) ----------
        // Example: { "bin": ["ps1","sega-cd"], "cue": ["ps1","sega-cd"] }
        if (ft.MultiMap != null)
        {
            foreach (var kvp in ft.MultiMap)
            {
                var ext = NormalizeExt(kvp.Key);
                if (string.IsNullOrEmpty(ext)) continue;

                var consoles = kvp.Value ?? new List<string>();
                if (!ExtToConsoles.TryGetValue(ext, out var list))
                {
                    list = new List<string>();
                    ExtToConsoles[ext] = list;
                }

                foreach (var c in consoles)
                {
                    if (!list.Contains(c, StringComparer.OrdinalIgnoreCase))
                        list.Add(c);
                }

                // Ensure this extension is globally known
                AllExtensions.Add(ext);
            }
        }

        // ---------- 4. Sanity cleanup ----------
        // Remove empty console lists, just in case
        foreach (var ext in ExtToConsoles.Keys.ToList())
        {
            var lst = ExtToConsoles[ext];
            if (lst.Count == 0)
                ExtToConsoles.Remove(ext);
        }
    }

    // ---------------------------------------------------------------------
    // Utility helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Normalize a file extension to lower case, without a leading dot.
    /// Examples:
    ///   ".sfc" → "sfc"
    ///   "ISO"  → "iso"
    /// </summary>
    public static string NormalizeExt(string extOrName)
    {
        if (string.IsNullOrWhiteSpace(extOrName)) return string.Empty;
        var e = extOrName.Trim();
        if (e.StartsWith(".")) e = e[1..];
        return e.ToLowerInvariant();
    }

    /// <summary>
    /// Extract normalized extension (no dot) from a full file path.
    /// Returns empty string if no extension found.
    /// </summary>
    public static string GetPathExtNoDot(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return string.Empty;
        return NormalizeExt(ext);
    }
}
