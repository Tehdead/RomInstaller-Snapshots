using System;
using System.Collections.Generic;

namespace RomInstaller.Core.Models;

/// <summary>
/// FileTypes
/// ----------
/// Contract for <c>filetypes.json</c>, which describes how file extensions map to
/// consoles and which extensions take precedence when multiple apply.
///
/// 🧩 Why this exists
///   • Data-driven console detection (no hardcoded extension tables in code)
///   • Supports ambiguous mappings (e.g., "bin" → ["ps1","sega-cd"])
///   • Allows global and per-console ordering for “best match” behavior
///
/// 🧱 Normalization rules
///   • All extensions are stored WITHOUT a leading dot (e.g., "iso", not ".iso").
///   • Keys in <see cref="PerConsole"/> and <see cref="MultiMap"/> should use
///     normalized console IDs (lowercase, kebab- or snake-case preferred).
///
/// ⚙️ Example shape:
/// {
///   "schema": 1,
///   "globalPriority": [ "iso", "cue", "bin", "sfc", "smc", "gba", "nds", "zip", "7z" ],
///   "perConsole": {
///     "ps1":      [ "cue", "bin", "iso" ],
///     "ps2":      [ "iso", "bin" ],
///     "sega-cd":  [ "bin", "cue" ],
///     "snes":     [ "sfc", "smc" ],
///     "gba":      [ "gba" ],
///     "nds":      [ "nds", "zip" ],
///     "gamecube": [ "iso", "gcm", "rvz" ],
///     "wii":      [ "iso", "wbfs" ],
///     "psp":      [ "iso", "cso" ]
///   },
///   "multiMap": {
///     "bin": [ "ps1", "sega-cd" ],
///     "cue": [ "ps1", "sega-cd" ],
///     "zip": [ "gba", "nds" ]
///   }
/// }
///
/// 🔍 Notes
///   • <see cref="GlobalPriority"/> is a cross-console tie-breaker when comparing different
///     extensions globally (lower index = higher priority).
///   • <see cref="PerConsole"/> is the authoritative list of valid extensions per console,
///     ordered by preference for that console.
///   • <see cref="MultiMap"/> is optional. If omitted, the index builder can infer overlaps
///     by scanning <see cref="PerConsole"/> (see FileTypesIndex).
/// </summary>
public class FileTypes
{
    /// <summary>
    /// Schema version for future migrations (always an integer).
    /// </summary>
    public int Schema { get; set; } = 1;

    /// <summary>
    /// Cross-console ranking of extensions. Earlier = more preferred globally.
    /// Extensions must be stored WITHOUT a leading dot (e.g., "iso", "cue").
    /// </summary>
    public List<string> GlobalPriority { get; set; } = [];

    /// <summary>
    /// Per-console allowed extensions and their order of preference.
    /// Keys are normalized console IDs (e.g., "ps1", "sega-cd").
    /// Values are extension lists WITHOUT leading dots.
    /// </summary>
    public Dictionary<string, List<string>> PerConsole { get; set; } = [];

    /// <summary>
    /// Optional cross-console mapping of extensions → consoles that accept them.
    /// If omitted, overlaps may be inferred from <see cref="PerConsole"/>.
    /// Values are console ID lists. Keys are extensions WITHOUT leading dots.
    /// </summary>
    public Dictionary<string, List<string>>? MultiMap { get; set; }
}
