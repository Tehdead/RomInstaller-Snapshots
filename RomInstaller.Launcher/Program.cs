using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using RomInstaller.Core;
using RomInstaller.Core.Models;
using RomInstaller.Core.Enums;
using RomInstaller.Core.Services;

//
// ──────────────────────────────────────────────────────────────
//  RomInstaller.Launcher
// ──────────────────────────────────────────────────────────────
//
// Purpose:
//   Launch an installed ROM using its associated emulator.
//
// Typical Invocation:
//   RomInstaller.Launcher.exe --key <manifestId>
//
// Core Responsibilities:
//   • Resolve game + emulator metadata from manifest/catalog
//   • Construct and safely launch emulator process
//   • Update manifest.LastPlayedAt timestamp post-launch
//   • Exit with the emulator’s exit code (for scripting chains)
//
// Design Goals:
//   • No UI
//   • No complex recovery logic
//   • Clean, predictable exits with well-defined codes
//

CorePaths.EnsureAppFolders();

try
{
    // ------------------------------------------------------------
    // Parse command-line arguments
    // ------------------------------------------------------------
    string[] argv = Environment.GetCommandLineArgs()[1..];

    if (argv.Length < 2 || !argv[0].Equals("--key", StringComparison.OrdinalIgnoreCase))
        Fail(ExitCodes.UsageError, "Usage: Launcher.exe --key <manifestId>");

    string key = argv[1];
    if (string.IsNullOrWhiteSpace(key))
        Fail(ExitCodes.UsageError, "ManifestId was empty.");

    // ------------------------------------------------------------
    // Load configuration (manifest, settings, emulator catalog)
    // ------------------------------------------------------------
    var manifest = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();
    var settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
    var catalog = Json.Load<EmulatorCatalog>(CorePaths.EmulatorCatalogPath) ?? new EmulatorCatalog();

    // Defensive: never let Games be null
    var gamesList = manifest.Games ?? [];

    // ------------------------------------------------------------
    // Locate target game
    // ------------------------------------------------------------
    var game = gamesList.FirstOrDefault(g => g.Id == key);
    if (game is null)
        Fail(ExitCodes.NotFound, $"Game not found in manifest: {key}");

    var nonNullGame = game!;
    if (string.IsNullOrWhiteSpace(nonNullGame.RomPath) || !File.Exists(nonNullGame.RomPath))
        Fail(ExitCodes.NotFound, $"ROM not found: {nonNullGame.RomPath}");
    if (string.IsNullOrWhiteSpace(nonNullGame.EmulatorId))
        Fail(ExitCodes.UsageError, "Game has no emulator ID assigned.");

    // ------------------------------------------------------------
    // Resolve emulator metadata
    // ------------------------------------------------------------
    var emu = catalog.Emulators
        .FirstOrDefault(e => e.Id.Equals(nonNullGame.EmulatorId, StringComparison.OrdinalIgnoreCase));
    if (emu is null)
        Fail(ExitCodes.UsageError, $"Emulator spec missing for id={nonNullGame.EmulatorId}");

    var nonNullEmu = emu!;

    // Resolve %EMULATION% variable → absolute emulation root
    var emulationRoot = CorePaths.ResolveEmulationRoot(settings.EmulationRoot);


    // Normalize slashes for current platform
    string? emulatorExe = nonNullEmu.PortablePath?
        .Replace("%EMULATION%", emulationRoot)
        .Replace("/", Path.DirectorySeparatorChar.ToString());

    if (string.IsNullOrWhiteSpace(emulatorExe) || !File.Exists(emulatorExe))
        Fail(ExitCodes.NotFound, $"Emulator executable not found at: {emulatorExe ?? "(null)"}");

    // ------------------------------------------------------------
    // Build final command-line arguments
    // ------------------------------------------------------------
    // ArgTemplate syntax supports {ROM}, which will be replaced with the quoted ROM path.
    // Example: "--fullscreen --nogui {ROM}" → "--fullscreen --nogui \"C:\Games\Super Metroid.sfc\""
    static string Quote(string p) => $"\"{p}\"";

    string template = nonNullEmu.ArgTemplate ?? "{ROM}";
    string romArg = Quote(nonNullGame.RomPath);

    string finalArgs = template.Contains("\"{ROM}\"", StringComparison.OrdinalIgnoreCase)
        ? template.Replace("\"{ROM}\"", romArg, StringComparison.OrdinalIgnoreCase)
        : template.Replace("{ROM}", romArg, StringComparison.OrdinalIgnoreCase);

    // Clean redundant spacing
    finalArgs = string.Join(' ', finalArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------
    // Prepare and launch the process
    // ------------------------------------------------------------
    string? workingDir = Path.GetDirectoryName(emulatorExe);
    if (string.IsNullOrEmpty(workingDir))
        Fail(ExitCodes.Fatal, $"Could not determine working directory for: {emulatorExe}");

    var psi = new ProcessStartInfo
    {
        FileName = emulatorExe,
        Arguments = finalArgs,
        WorkingDirectory = workingDir,
        UseShellExecute = false // required for proper logging/redirection
    };

    Logger.Info($"Launching emulator: \"{emulatorExe}\" {finalArgs} (cwd: {workingDir})");

    try
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        proc.WaitForExit();

        // --------------------------------------------------------
        // Post-launch: update LastPlayedAt (non-fatal on failure)
        // --------------------------------------------------------
        try
        {
            var updated = gamesList
                .Select(g => g.Id == nonNullGame.Id ? g with { LastPlayedAt = DateTime.UtcNow } : g)
                .ToList();

            Json.Save(CorePaths.ManifestPath, manifest with { Games = updated });
        }
        catch (Exception ex)
        {
            Logger.Exception("Manifest update after launch", ex);
        }

        Environment.Exit(proc.ExitCode);
    }
    catch (Exception ex)
    {
        Logger.Exception("Launch failure", ex);
        Fail(ExitCodes.LaunchError, $"Launch error: {ex.Message}");
    }
}
catch (Exception ex)
{
    Logger.Exception("Launcher fatal", ex);
    Console.Error.WriteLine("Fatal launcher error. See logs for details.");
    Environment.Exit((int)ExitCodes.Fatal);
}

/// <summary>
/// Unified fatal/usage handler: logs, optionally shows UI error box,
/// and terminates with the given exit code.
/// </summary>
static void Fail(ExitCodes code, string message)
{
    Console.Error.WriteLine(message);
    Logger.Error($"{code}: {message}");

    try
    {
        if (!Console.IsOutputRedirected)
            RomInstaller.Core.Services.UserNotify.ErrorBox("RomInstaller", message);
    }
    catch
    {
        // Never allow UI notification to crash the launcher
    }

    Environment.Exit((int)code);
}
