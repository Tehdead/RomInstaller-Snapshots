using System;
using System.IO;

namespace RomInstaller.Core.Services;

/// <summary>
/// Logger
/// -------
/// Centralized, crash-proof logging service for **RomInstaller**.
///
/// 🧭 Purpose:
///  • Persist important runtime messages, warnings, and exceptions  
///  • Ensure logs are never the cause of a crash — even under disk or I/O errors  
///  • Provide simple log categorization (`INFO`, `WARN`, `ERROR`)  
///
/// 🧱 Design Philosophy:
///  • Completely static (thread-safe via internal lock)  
///  • One log file per UTC day: `log-YYYYMMDD.log`  
///  • Tolerates concurrent writes from CLI, Launcher, and Core services  
///  • Never throws exceptions outward — failure to log is silently ignored  
///
/// Example usage:
/// ```csharp
/// Logger.Info("Installer initialized.");
/// Logger.Warn("Duplicate ROM detected, skipping copy.");
/// Logger.Exception("Install failure", ex);
/// ```
/// </summary>
public static class Logger
{
    /// <summary>Thread-synchronization lock for safe concurrent writes.</summary>
    private static readonly object _lock = new();

    /// <summary>
    /// The log file path for the current UTC day.
    /// Format: `%AppData%\RomInstaller\logs\log-YYYYMMDD.log`
    /// </summary>
    private static string LogFilePath =>
        Path.Combine(CorePaths.LogsDir, $"log-{DateTime.UtcNow:yyyyMMdd}.log");

    /// <summary>Write a normal informational message (expected operation).</summary>
    public static void Info(string msg) => Write("INFO", msg);

    /// <summary>Write a warning message (recoverable condition).</summary>
    public static void Warn(string msg) => Write("WARN", msg);

    /// <summary>Write an error message (non-fatal failure or handled exception).</summary>
    public static void Error(string msg) => Write("ERROR", msg);

    /// <summary>
    /// Logs an exception with contextual information.
    /// Does *not* rethrow or modify control flow.
    ///
    /// Example:
    /// ```csharp
    /// try { ... } catch (Exception ex) { Logger.Exception("Installer.Apply", ex); }
    /// ```
    /// </summary>
    public static void Exception(string context, Exception ex)
        => Write("ERROR", $"{context}: {ex.Message}\n{ex}");

    /// <summary>
    /// Core write method used by all log levels.
    ///
    /// 🧩 Behavior:
    ///  • Ensures log directory exists  
    ///  • Appends timestamped entry in ISO-8601 UTC format  
    ///  • Never throws exceptions outward  
    /// </summary>
    private static void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(CorePaths.LogsDir);

                // Example line:
                // 2025-10-04T13:27:58.1234567Z [INFO] Installed 'Super Metroid' to Emulation/SNES/ROMs
                File.AppendAllText(LogFilePath,
                    $"{DateTime.UtcNow:O} [{level}] {msg}\n");
            }
        }
        catch
        {
            // Intentionally swallowed:
            // Logging must *never* interfere with core functionality.
            // (e.g., low disk, locked file, permissions, etc.)
        }
    }
}
