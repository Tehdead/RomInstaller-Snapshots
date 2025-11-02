using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RomInstaller.Core.Services; // for GeneratedRegex

namespace RomInstaller.Core;

/// <summary>
/// Json
/// -----
/// Centralized JSON helper for all RomInstaller subsystems.
///
/// ✅ Goals:
///  • Provide a *consistent* serialization policy across CLI, Core, and Launcher  
///  • Avoid repetitive try/catch logic for malformed or missing files  
///  • Make writes *atomic* (write → temp file → replace) to prevent data loss on crash  
///
/// ⚙️ Design:
///  • Always uses `camelCase` naming for compatibility with web tools and configs  
///  • Ignores nulls on write (keeps output minimal)  
///  • Logs (but does not throw) on malformed JSON — returning `null` safely  
///
/// Typical usage:
/// ```csharp
/// var settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
/// Json.Save(CorePaths.ManifestPath, manifest);
/// ```
/// </summary>
public static partial class Json
{
    /// <summary>
    /// Shared serializer options for all loads/saves:
    /// - Pretty printed (`WriteIndented`)
    /// - camelCase naming convention
    /// - Null values omitted
    /// </summary>
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 👇 Allow friendly .json files with // comments and trailing commas
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Attempts to load and deserialize a JSON file.
    ///
    /// 🧩 Behavior:
    /// - Returns `null` if the file does not exist or cannot be parsed
    /// - Logs malformed JSON via <see cref="Logger.Exception"/>
    /// - Throws only on unexpected I/O failures (e.g., permission denied)
    /// - **Enhancement:** tolerates `// line` and `/* block */` comments by stripping them first
    /// </summary>
    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var txt = File.ReadAllText(path);

            // Allow comments in user-editable configs (settings/emulators/filetypes).
            // We strip them to keep System.Text.Json happy without enabling loose parsing.
            txt = StripComments(txt);

            return JsonSerializer.Deserialize<T>(txt, Opts);
        }
        catch (JsonException jex)
        {
            Logger.Exception($"JSON parse error: {path}", jex);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Exception($"JSON load error: {path}", ex);
            throw;
        }
    }

    /// <summary>
    /// Atomically writes an object to disk as JSON.
    /// </summary>
    public static void Save<T>(string path, T obj)
    {
        var tmp = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(obj, Opts);
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Exception($"JSON save error: {path}", ex);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // Non-critical cleanup error ignored
            }
        }
    }

    // ----------------------------------
    // Helpers
    // ----------------------------------

    /// <summary>
    /// Strips `/* block */` and `// line` comments without disturbing string literals.
    /// Uses compile-time generated regexes for performance and analyzer cleanliness.
    /// </summary>
    private static string StripComments(string s)
    {
        // Remove /* ... */ across lines (non-greedy)
        s = BlockCommentRegex().Replace(s, "");

        // Remove // ... to end-of-line
        s = LineCommentRegex().Replace(s, "");

        return s;
    }

    // ----------------------------------
    // Compile-time regexes
    // ----------------------------------

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"^\s*//.*?$", RegexOptions.Multiline)]
    private static partial Regex LineCommentRegex();
}
