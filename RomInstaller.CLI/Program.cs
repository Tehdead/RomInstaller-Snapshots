using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using RomInstaller.Core;
using RomInstaller.Core.Models;
using RomInstaller.Core.Services;
using RomInstaller.Core.Enums;

/// <summary>
/// RomInstaller.CLI
/// ----------------
/// Entry point for the command-line interface: routes commands,
/// coordinates plan/apply, and delegates core logic to Planner/Installer.
///
/// Design:
///   • Keep the top-level flow readable and flat
///   • Domain exceptions -> neat console errors (usage/fixable issues)
///   • Unexpected exceptions -> stable logging + fatal exit
///   • Predictable ExitCodes for scripting/Explorer integration
/// </summary>
CorePaths.EnsureAppFolders();
SeedIfMissing();

try
{
    // Parse command-line args (skip [0] = exe path).
    string[] argv = Environment.GetCommandLineArgs()[1..];

    // No args → print help
    if (argv.Length == 0) { PrintHelp(); Environment.Exit((int)ExitCodes.Ok); }

    if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine(ver);
        Environment.Exit((int)ExitCodes.Ok);
    }


    // Route primary verb
    switch (argv[0].ToLowerInvariant())
    {
        // ------------------------------------------------------------
        // rom install "<path>" [--apply] [--move|--copy]
        //               [--console <id>|--console=<id>]
        //               [--emulator <id>|--emulator=<id>]
        //               [--shortcut | --no-shortcut]
        // ------------------------------------------------------------
        case "install":
            {
                if (argv.Length < 2)
                    Fail(ExitCodes.UsageError,
                        "Missing path. Usage: rom install \"<path-to-rom>\" [--apply] [--move|--copy] [--console <id>] [--emulator <id>] [--shortcut|--no-shortcut]");

                // Optional flags
                var apply = argv.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
                var move = argv.Any(a => a.Equals("--move", StringComparison.OrdinalIgnoreCase));
                var copy = argv.Any(a => a.Equals("--copy", StringComparison.OrdinalIgnoreCase));

                // Mutually exclusive copy/move safety
                if (move && copy)
                    Fail(ExitCodes.UsageError, "Choose either --move or --copy (default is --copy).");

                // Optional overrides (support both --name value and --name=value)
                var consoleOverride = GetOptValue(argv, "--console");
                var emulatorOverride = GetOptValue(argv, "--emulator");

                // Optional shortcut policy (mutually exclusive). Default = skip unless forced.
                bool wantsShortcut = argv.Any(a => a.Equals("--shortcut", StringComparison.OrdinalIgnoreCase)
                                                   || a.Equals("--create-shortcut", StringComparison.OrdinalIgnoreCase));
                bool wantsNoShortcut = argv.Any(a => a.Equals("--no-shortcut", StringComparison.OrdinalIgnoreCase));
                if (wantsShortcut && wantsNoShortcut)
                    Fail(ExitCodes.UsageError, "Choose either --shortcut or --no-shortcut (not both).");
                bool? shortcutOverride = wantsShortcut ? true : wantsNoShortcut ? false : (bool?)null;

                // Dry-run or full install
                if (apply) DoInstallApply(argv[1], move, consoleOverride, emulatorOverride, shortcutOverride);
                else DoInstallPlan(argv[1], consoleOverride, emulatorOverride);
                break;
            }


        // ------------------------------------------------------------
        // rom create-shortcut --key <manifestId>
        // ------------------------------------------------------------
        case "create-shortcut":
            if (argv.Length < 3 || argv[1] != "--key")
                Fail(ExitCodes.UsageError, "Usage: rom create-shortcut --key <manifestId>");
            DoCreateShortcut(argv[2]);
            break;

        // ------------------------------------------------------------
        // rom prune-manifest
        // Remove entries whose RomPath no longer exists on disk.
        // ------------------------------------------------------------
        case "prune-manifest":
            PruneManifest();
            break;

        // ------------------------------------------------------------
        // Explorer integration (context menu)
        // ------------------------------------------------------------
        case "register-context":
            RegisterContext(argv.Skip(1));
            break;

        case "unregister-context":
            UnregisterContext(argv.Skip(1));
            break;

        case "validate-config":
            ValidateConfig();
            break;

        // ------------------------------------------------------------
        // rom version
        // Displays current RomInstaller version and seed metadata
        // ------------------------------------------------------------
        case "version":
            ShowVersion(argv.Skip(1));
            break;

        // ------------------------------------------------------------
        // rom list [--json] [--console <id>|--console=<id>] [--emulator <id>|--emulator=<id>]
        //          [--no-shortcut] [--has-shortcut]
        // ------------------------------------------------------------
        case "list":
            ListGames(argv.Skip(1));
            break;

        case "uninstall":
            DoUninstall(argv.Skip(1));
            break;

        case "register-shortcut-verb":
            {
                string? exeMaybe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exeMaybe))
                    Fail(ExitCodes.Fatal, "Cannot determine CLI executable path.");
                if (RegistryService.RegisterUninstallVerbForShortcuts(exeMaybe!, out var err))
                {
                    Console.WriteLine("Registered 'Uninstall ROM' for shortcuts.");
                    Environment.Exit((int)ExitCodes.Ok);
                }
                else
                {
                    Console.Error.WriteLine(err ?? "Failed to register uninstall verb.");
                    Environment.Exit((int)ExitCodes.UsageError);
                }
                break;
            }

        case "unregister-shortcut-verb":
            {
                if (RegistryService.UnregisterUninstallVerbForShortcuts(out var err))
                {
                    Console.WriteLine("Unregistered 'Uninstall ROM' for shortcuts.");
                    Environment.Exit((int)ExitCodes.Ok);
                }
                else
                {
                    Console.Error.WriteLine(err ?? "Failed to unregister uninstall verb.");
                    Environment.Exit((int)ExitCodes.UsageError);
                }
                break;
            }

        default:
            Fail(ExitCodes.UsageError, $"Unknown command '{argv[0]}'.");
            break;
    }
}
catch (RomInstallerException rex)
{
    // Controlled, expected errors (validation/usage/etc.)
    Logger.Exception("CLI domain error", rex);
    Console.Error.WriteLine(rex.Message);
    Environment.Exit((int)ExitCodes.UsageError);
}
catch (Exception ex)
{
    // Unexpected crash-level failure
    Logger.Exception("CLI fatal", ex);
    Console.Error.WriteLine("Fatal error. See logs for details.");
    Environment.Exit((int)ExitCodes.Fatal);
}

#region Command Handlers

/// <summary>
/// Dry-run: build and print an InstallPlan (no file movement).
/// If plan needs prompt, attempt to launch the Resolve UI.
/// </summary>
static void DoInstallPlan(string sourcePath, string? consoleOverride, string? emulatorOverride)
{
    if (string.IsNullOrWhiteSpace(sourcePath))
        Fail(ExitCodes.UsageError, "Source path is empty.");
    if (!File.Exists(sourcePath))
        Fail(ExitCodes.NotFound, $"File not found: {sourcePath}");

    var settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
    var catalog = Json.Load<EmulatorCatalog>(CorePaths.EmulatorCatalogPath) ?? new EmulatorCatalog();
    var ft = Json.Load<FileTypes>(CorePaths.FiletypesPath) ?? new FileTypes();
    var ftIndex = new FileTypesIndex(ft);

    var planner = new Planner(settings, catalog, ftIndex);
    var plan = planner.PlanFromFile(sourcePath, consoleOverride, emulatorOverride);

    if (plan.NeedsPrompt)
    {
        var reasons = FormatNeedsPromptReasons(plan);

        // Try launching the Resolve UI so the user can pick console/emulator.
        if (TryLaunchResolveUi(sourcePath, consoleOverride, emulatorOverride))
        {
            // Return quickly for Explorer verb responsiveness. UI continues the flow.
            Environment.Exit((int)ExitCodes.NeedsPrompt);
        }

        // Last-resort guidance if UI launch failed (e.g., UI missing)
        if (!Console.IsOutputRedirected)
        {
            RomInstaller.Core.Services.UserNotify.ErrorBox(
                "RomInstaller – More info needed",
                $"Cannot auto-install \"{Path.GetFileName(sourcePath)}\".\n{reasons}\n\n" +
                "Fix:\n" +
                " • Set a default emulator in settings.json (defaultEmulatorPerConsole)\n" +
                " • Or re-run:\n" +
                $"   rom install \"{sourcePath}\" --console <id> --emulator <id> --apply"
            );
        }
    }

    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, Json.Opts));
    Environment.Exit(plan.NeedsPrompt ? (int)ExitCodes.NeedsPrompt : (int)ExitCodes.Ok);
}

/// <summary>
/// Perform the actual install (copy/move), update manifest, and print result.
/// If plan needs prompt, try the Resolve UI.
///
/// NEW: respects per-call shortcut override (--shortcut / --no-shortcut) by
/// adjusting Settings.AutoCreateShortcut *before* passing to Installer,
/// so only one component is responsible for creating the shortcut.
/// </summary>
static void DoInstallApply(string sourcePath, bool move, string? consoleOverride, string? emulatorOverride, bool? shortcutOverride)

{
    if (string.IsNullOrWhiteSpace(sourcePath))
        Fail(ExitCodes.UsageError, "Source path is empty.");
    if (!File.Exists(sourcePath))
        Fail(ExitCodes.NotFound, $"File not found: {sourcePath}");

    var settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
    var catalog = Json.Load<EmulatorCatalog>(CorePaths.EmulatorCatalogPath) ?? new EmulatorCatalog();
    var ft = Json.Load<FileTypes>(CorePaths.FiletypesPath) ?? new FileTypes();
    var ftIndex = new FileTypesIndex(ft);

    var plan = new Planner(settings, catalog, ftIndex).PlanFromFile(sourcePath, consoleOverride, emulatorOverride);
    if (plan.NeedsPrompt)
    {
        var reasons = FormatNeedsPromptReasons(plan);

        // Attempt to launch the Resolve UI so the user can choose.
        if (TryLaunchResolveUi(sourcePath, consoleOverride, emulatorOverride))
            Environment.Exit((int)ExitCodes.NeedsPrompt);

        // Fallback guidance
        if (!Console.IsOutputRedirected)
        {
            RomInstaller.Core.Services.UserNotify.ErrorBox(
                "RomInstaller – More info needed",
                $"Cannot auto-install \"{Path.GetFileName(sourcePath)}\".\n{reasons}\n\n" +
                "Fix:\n" +
                " • Set a default emulator in settings.json (defaultEmulatorPerConsole)\n" +
                " • Or re-run:\n" +
                $"   rom install \"{sourcePath}\" --console <id> --emulator <id> --apply"
            );
        }

        Console.Error.WriteLine("Plan requires user input (ambiguous console/emulator). Run dry-run to inspect.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, Json.Opts));
        Environment.Exit((int)ExitCodes.NeedsPrompt);
    }

    var installer = new Installer(settings, catalog);
    try
    {
        var entry = installer.Apply(plan, move: move, out var msg);
        Console.WriteLine(msg ?? "Installed.");
        Console.WriteLine($"ManifestId: {entry.Id}");

        // ---------- Optional desktop shortcut behavior ----------
        // Policy precedence:
        //   1) Per-call override (--shortcut / --no-shortcut)
        //   2) settings.AutoCreateShortcut (user preference)
        //   3) Default fallback: false (safety)
        var wantShortcut = shortcutOverride ?? settings.AutoCreateShortcut;

        if (wantShortcut)
        {
            if (TryCreateShortcutInternal(entry.Id, out var shortcutPath, out var err))
            {
                Console.WriteLine($"Shortcut: created ({shortcutPath})");
            }
            else
            {
                Console.WriteLine("Shortcut: requested but failed.");
                if (!string.IsNullOrWhiteSpace(err))
                    Console.Error.WriteLine(err);
            }
        }
        else
        {
            var policy = shortcutOverride is null
                ? $"settings ({settings.AutoCreateShortcut.ToString().ToLowerInvariant()})"
                : (shortcutOverride.Value ? "--shortcut" : "--no-shortcut");

            Console.WriteLine($"Shortcut: {(wantShortcut ? "created" : "skipped")} (policy: {policy})");
        }

        Environment.Exit((int)ExitCodes.Ok);
    }
    catch (RomInstallerException rex)
    {
        Console.Error.WriteLine(rex.Message);
        Logger.Exception("InstallApply domain error", rex);
        Environment.Exit((int)ExitCodes.UsageError);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Install failed. See logs for details.");
        Logger.Exception("InstallApply fatal", ex);
        Environment.Exit((int)ExitCodes.Fatal);
    }
}

/// <summary>
/// Uninstall a ROM from the manifest (and optionally remove files & shortcut).
/// Usage:
///   rom uninstall --key <manifestId> [--permanent] [--keep-files]
///   rom uninstall --shortcut "<path.lnk>" [--permanent] [--keep-files]
///   rom uninstall --shortcut "<path.lnk>" --remove-shortcut-only
/// </summary>
static void DoUninstall(IEnumerable<string> rest)
{
    // Parse flags
    var args = rest.ToArray();

    // Identification: by manifestId OR shortcut path
    var key = GetOptValue(args, "--key");
    var shortcutPath = GetOptValue(args, "--shortcut");

    var permanent = args.Any(a => a.Equals("--permanent", StringComparison.OrdinalIgnoreCase));
    var keepFiles = args.Any(a => a.Equals("--keep-files", StringComparison.OrdinalIgnoreCase));
    var shortcutOnly = args.Any(a => a.Equals("--remove-shortcut-only", StringComparison.OrdinalIgnoreCase));

    if (shortcutOnly && (keepFiles || permanent))
    {
        // shortcut-only ignores file deletion flags
        // (we still honor --permanent vs recycle when deleting the .lnk itself)
        keepFiles = true; // ensures we won't touch game files/manifest
    }

    // Resolve key from shortcut if needed
    if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(shortcutPath))
    {
        if (!ShortcutService.TryReadShortcut(shortcutPath!, out var target, out var arguments))
            Fail(ExitCodes.UsageError, $"Could not read shortcut: {shortcutPath}");

        // Our launcher shortcuts have args like:  --key <manifestId>
        // Simple parse:
        key = ExtractKeyFromArgs(arguments);
        if (string.IsNullOrWhiteSpace(key))
            Fail(ExitCodes.UsageError, "Shortcut did not contain a --key <manifestId> argument.");
    }

    if (string.IsNullOrWhiteSpace(key))
        Fail(ExitCodes.UsageError, "Missing --key <manifestId> or --shortcut \"path.lnk\".");

    // Load manifest & find entry
    var manifest = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();
    var entry = manifest.Games.FirstOrDefault(g => string.Equals(g.Id, key, StringComparison.OrdinalIgnoreCase));
    if (entry is null)
        Fail(ExitCodes.NotFound, $"Manifest entry not found for id: {key}");

    // Delete assets unless keepFiles || shortcutOnly
    if (!keepFiles && !shortcutOnly)
    {
        // Prefer removing the entire GameFolder (cleaner), else just the ROM file
        if (!string.IsNullOrWhiteSpace(entry.GameFolder) && Directory.Exists(entry.GameFolder))
        {
            DeletionService.DeleteDirectory(entry.GameFolder, permanent);
        }
        else if (!string.IsNullOrWhiteSpace(entry.RomPath) && File.Exists(entry.RomPath))
        {
            DeletionService.DeleteFile(entry.RomPath, permanent);
            // Try to remove empty parent
            TryDeleteDirIfEmpty(Path.GetDirectoryName(entry.RomPath));
        }
    }

    // Remove shortcut: either from manifest path or provided --shortcut
    // (We always attempt to remove the discovered shortcut unless user requested shortcutOnly=false and keepFiles=true? No, still safe.)
    var shortcutCandidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(shortcutPath)) shortcutCandidates.Add(shortcutPath!);
    if (!string.IsNullOrWhiteSpace(entry.ShortcutPath)) shortcutCandidates.Add(entry.ShortcutPath!);

    foreach (var lnk in shortcutCandidates.Where(p => !string.IsNullOrWhiteSpace(p)))
    {
        if (File.Exists(lnk))
            DeletionService.DeleteFile(lnk, permanent);
    }

    // Update manifest unless shortcutOnly set
    if (!shortcutOnly)
    {
        manifest = manifest with
        {
            Games = manifest.Games.Where(g => !string.Equals(g.Id, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList()
        };
        Json.Save(CorePaths.ManifestPath, manifest);
    }

    Console.WriteLine(shortcutOnly
        ? "Shortcut removed."
        : "Uninstall completed.");
    Environment.Exit((int)ExitCodes.Ok);

    // --- local helper ---
    static string? ExtractKeyFromArgs(string? argsText)
    {
        if (string.IsNullOrWhiteSpace(argsText)) return null;

        // Accept both:  --key <id>   and   --key="<id>"
        var parts = argsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.StartsWith("--key=", StringComparison.OrdinalIgnoreCase))
            {
                var v = p["--key=".Length..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            if (p.Equals("--key", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            {
                var v = parts[i + 1].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }
}

/// <summary>
/// Create a desktop shortcut targeting the Launcher, then stamp its path into the manifest.
/// </summary>
static void DoCreateShortcut(string manifestId)
{
    if (TryCreateShortcutInternal(manifestId, out var shortcutPath, out var error))
    {
        Console.WriteLine($"Shortcut created: {shortcutPath}");
        Environment.Exit((int)ExitCodes.Ok);
    }
    else
    {
        Console.Error.WriteLine(error ?? "Shortcut creation failed.");
        Environment.Exit((int)ExitCodes.StartFailure);
    }
}

static bool TryCreateShortcutInternal(string manifestId, out string? shortcutPath, out string? error)
{
    shortcutPath = null;
    error = null;

    try
    {
        var manifest = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();
        var gamesList = manifest.Games ?? [];
        var game = gamesList.FirstOrDefault(g => g.Id == manifestId);
        if (game is null)
        {
            error = $"Game not found: {manifestId}";
            return false;
        }

        // Locate launcher executable (dev layout first, then solution layout)
        var cliExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(cliExe))
        {
            error = "Cannot determine CLI executable path.";
            return false;
        }

        var baseDirMaybe = Path.GetDirectoryName(cliExe);
        if (string.IsNullOrEmpty(baseDirMaybe))
        {
            error = "Cannot determine CLI base directory.";
            return false;
        }
        var baseDir = baseDirMaybe!;

        var siblingDir = baseDir.Replace("\\RomInstaller.CLI", "\\RomInstaller.Launcher");
        var launcherExe = Path.Combine(siblingDir, "RomInstaller.Launcher.exe");

        // Title fallback
        var titleCandidate = game.Title ?? string.Empty;
        var safeTitle = string.IsNullOrWhiteSpace(titleCandidate) ? "Game" : titleCandidate;

        // Fallback to parent layout (solution-level bin)
        if (string.IsNullOrEmpty(launcherExe) || !File.Exists(launcherExe))
        {
            var parent = Directory.GetParent(baseDir);
            if (parent is not null)
            {
                var alt = Path.Combine(parent.FullName, "RomInstaller.Launcher", "RomInstaller.Launcher.exe");
                if (File.Exists(alt)) launcherExe = alt;
            }
        }

        if (string.IsNullOrEmpty(launcherExe) || !File.Exists(launcherExe))
        {
            error = $"Launcher not found. Looked in:\n- {launcherExe}";
            return false;
        }

        // Create shortcut on Desktop first
        var args = $"--key {manifestId}";
        const string appId = "Aurion.RomInstaller";
        string? icon = null;

        shortcutPath = ShortcutService.CreateDesktopShortcut(safeTitle, launcherExe, args, icon, appId);

        // Optionally move it to settings.ShortcutsRoot
        var settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
        shortcutPath = MoveShortcutToRootIfConfigured(shortcutPath, settings);

        // Persist into manifest entry (Games is init-only => rebuild list)
        var localShortcutPath = shortcutPath; // capture-safe local
        var gameId = string.IsNullOrEmpty(game.Id) ? manifestId : game.Id;
        var updated = (manifest.Games ?? [])
            .Select(g => ((g.Id ?? string.Empty) == gameId)
                ? g with { ShortcutPath = localShortcutPath }
                : g)
            .ToList();

        Json.Save(CorePaths.ManifestPath, manifest with { Games = updated });
        return true;
    }
    catch (Exception ex)
    {
        Logger.Exception("TryCreateShortcutInternal", ex);
        error = ex.Message;
        return false;
    }
}

/// <summary>
/// Show current RomInstaller version/build info and seed/config presence.
/// Supports --json for machine-readable output.
/// </summary>
static void ShowVersion(IEnumerable<string> rest)
{
    bool asJson = rest.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));

    var versionPath = Path.Combine(CorePaths.AppDataRoot, "version.json");
    Dictionary<string, object> vdict = Json.Load<Dictionary<string, object>>(versionPath) ?? [];

    static string Get(IDictionary<string, object> d, string key, string fallback = "")
        => d.TryGetValue(key, out var v) ? (v?.ToString() ?? fallback) : fallback;

    var version = Get(vdict, "version", "?");
    var buildTag = Get(vdict, "buildTag", "Unlabeled");
    var buildDate = Get(vdict, "buildDate", "?");
    var author = Get(vdict, "author", "Unknown");
    var commit = Get(vdict, "commit", "n/a");
    var schemaVer = Get(vdict, "schemaVersion", "1");

    // Presence by existence; schema by best-effort parse
    bool settingsExists = File.Exists(CorePaths.SettingsPath);
    bool catalogExists = File.Exists(CorePaths.EmulatorCatalogPath);
    bool filetypesExists = File.Exists(CorePaths.FiletypesPath);

    var settings = Json.Load<Settings>(CorePaths.SettingsPath);
    var catalog = Json.Load<EmulatorCatalog>(CorePaths.EmulatorCatalogPath);
    var filetypes = Json.Load<FileTypes>(CorePaths.FiletypesPath);

    int? settingsSchema = settings?.Schema;
    int? catalogSchema = catalog?.Schema;
    int? filetypesSchema = filetypes?.Schema;

    if (asJson)
    {
        var payload = new
        {
            version,
            buildTag,
            buildDate,
            author,
            commit,
            schemaVersion = schemaVer,
            paths = new
            {
                versionJson = versionPath,
                settings = CorePaths.SettingsPath,
                emulators = CorePaths.EmulatorCatalogPath,
                filetypes = CorePaths.FiletypesPath
            },
            seeds = new
            {
                settings = new { present = settingsExists, schema = settingsSchema },
                emulators = new { present = catalogExists, schema = catalogSchema },
                filetypes = new { present = filetypesExists, schema = filetypesSchema }
            }
        };

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, Json.Opts));
        Environment.Exit((int)ExitCodes.Ok);
        return;
    }

    Console.WriteLine("RomInstaller CLI");
    Console.WriteLine("-----------------------------");

    WriteKV("Version", version);
    WriteKV("Build Tag", buildTag);
    WriteKV("Build Date", buildDate);
    WriteKV("Commit", commit);
    WriteKV("Schema", schemaVer);

    Console.WriteLine();
    WriteSection("Seeds & Config");
    WriteCheck("settings.json", settingsExists, settingsSchema);
    WriteCheck("emulators.json", catalogExists, catalogSchema);
    WriteCheck("filetypes.json", filetypesExists, filetypesSchema);

    Console.WriteLine();
    WriteSection("Paths");
    WriteKV("version.json", versionPath);
    WriteKV("settings", CorePaths.SettingsPath);
    WriteKV("emulators", CorePaths.EmulatorCatalogPath);
    WriteKV("filetypes", CorePaths.FiletypesPath);

    Console.WriteLine();
    Console.WriteLine("Use `rom version --json` for machine-readable output.");
    Environment.Exit((int)ExitCodes.Ok);

    // ------- local helpers (static to avoid captures/warnings) -------
    static void WithColor(ConsoleColor color, Action body)
    {
        var old = Console.ForegroundColor;
        try { Console.ForegroundColor = color; body(); }
        finally { Console.ForegroundColor = old; }
    }

    static void WriteKV(string key, string value)
    {
        WithColor(ConsoleColor.DarkGray, () => Console.Write($"{key,-12} "));
        Console.WriteLine(value);
    }

    static void WriteSection(string title)
    {
        WithColor(ConsoleColor.Cyan, () => Console.WriteLine(title));
    }

    static void WriteCheck(string name, bool present, int? schema)
    {
        if (present)
        {
            WithColor(ConsoleColor.Green, () => Console.Write("  ✓ "));
            Console.Write(name);
            if (schema is int s)
            {
                Console.Write("  ");
                WithColor(ConsoleColor.DarkGray, () => Console.Write($"(schema {s})"));
            }
            else
            {
                Console.Write("  ");
                WithColor(ConsoleColor.DarkYellow, () => Console.Write("(schema unknown)"));
            }
            Console.WriteLine();
        }
        else
        {
            WithColor(ConsoleColor.Yellow, () => Console.Write("  ✗ "));
            Console.Write(name);
            WithColor(ConsoleColor.DarkGray, () => Console.Write("  (missing)"));
            Console.WriteLine();
        }
    }
}

/// <summary>
/// List manifest entries with optional filters and JSON output.
/// Filters:
///   --console <id>   : only games for console id (e.g., ps1, ps2)
///   --emulator <id>  : only games using a specific emulator id
///   --no-shortcut    : only entries missing a ShortcutPath
///   --has-shortcut   : only entries that have a ShortcutPath
/// Output:
///   --json           : machine-readable array of entries
/// </summary>
/// <summary>
/// List games from manifest with optional filters.
///   rom list [--json] [--console <id>] [--emulator <id>] [--no-shortcut]
///
/// Filters:
///   --console <id>    : only that console (e.g., ps1, ps2, snes)
///   --emulator <id>   : only that emulator id (e.g., duckstation, pcsx2)
///   --no-shortcut     : only entries with missing/absent ShortcutPath
///
/// Output:
///   default  : pretty table
///   --json   : machine-readable JSON array
/// </summary>
static void ListGames(IEnumerable<string> rest)
{
    var args = rest.ToArray();
    bool asJson = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));

    var consoleFilter = GetOptValue(args, "--console");
    var emuFilter = GetOptValue(args, "--emulator");
    bool onlyNoShortcut = args.Any(a =>
        a.Equals("--no-shortcut", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("--missing-shortcut", StringComparison.OrdinalIgnoreCase));

    var manifest = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();
    var games = (manifest.Games ?? []).AsEnumerable();

    if (!string.IsNullOrWhiteSpace(consoleFilter))
        games = games.Where(g => g.Console.Equals(consoleFilter, StringComparison.OrdinalIgnoreCase));

    if (!string.IsNullOrWhiteSpace(emuFilter))
        games = games.Where(g => g.EmulatorId.Equals(emuFilter, StringComparison.OrdinalIgnoreCase));

    if (onlyNoShortcut)
        games = games.Where(g => string.IsNullOrWhiteSpace(g.ShortcutPath) || !File.Exists(g.ShortcutPath));

    var list = games.ToList();

    if (asJson)
    {
        var payload = list.Select(g => new
        {
            id = g.Id,
            title = g.Title,
            console = g.Console,
            emulatorId = g.EmulatorId,
            romPath = g.RomPath,
            gameFolder = g.GameFolder,
            shortcutPath = g.ShortcutPath,
            installedAt = g.InstalledAt,
            lastPlayedAt = g.LastPlayedAt
        });
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, Json.Opts));
        Environment.Exit((int)ExitCodes.Ok);
        return;
    }

    // ----- pretty table -----
    if (list.Count == 0)
    {
        Console.WriteLine("No matching games.");
        Environment.Exit((int)ExitCodes.Ok);
        return;
    }

    static string B(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s!;
    static string FileBase(string? p) => string.IsNullOrWhiteSpace(p) ? "-" : Path.GetFileName(p);

    // compute column widths with sane caps
    int wTitle = Math.Min(Math.Max(5, list.Max(g => (g.Title ?? "").Length)), 36);
    int wConsole = Math.Min(Math.Max(7, list.Max(g => (g.Console ?? "").Length)), 12);
    int wEmu = Math.Min(Math.Max(8, list.Max(g => (g.EmulatorId ?? "").Length)), 16);
    int wRom = Math.Min(Math.Max(5, list.Max(g => FileBase(g.RomPath).Length)), 40);
    // Shortcut column is just ✓/✗

    string H(string h, int w) => h.Length > w ? h[..w] : h.PadRight(w);
    string T(string s, int w) { s ??= ""; if (s.Length > w) return s[..(w - 1)] + "…"; return s.PadRight(w); }

    // header
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(
        $"{H("Title", wTitle)}  {H("Console", wConsole)}  {H("Emulator", wEmu)}  {H("ROM", wRom)}  Shortcut"
    );
    Console.ResetColor();

    // rows
    foreach (var g in list.OrderBy(g => g.Console).ThenBy(g => g.Title))
    {
        var hasShortcut = !string.IsNullOrWhiteSpace(g.ShortcutPath) && File.Exists(g.ShortcutPath);
        var mark = hasShortcut ? "✓" : "✗";
        Console.WriteLine(
            $"{T(B(g.Title), wTitle)}  {T(B(g.Console), wConsole)}  {T(B(g.EmulatorId), wEmu)}  {T(FileBase(g.RomPath), wRom)}  {mark}"
        );
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("Tips: add --json for machine output; filter with --console <id>, --emulator <id>, or --no-shortcut.");
    Console.ResetColor();

    Environment.Exit((int)ExitCodes.Ok);
}


/// <summary>
/// Remove manifest entries whose RomPath no longer exists (user deleted/moved files).
/// </summary>
static void PruneManifest()
{
    var manifest = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();
    var current = manifest.Games ?? [];

    var kept = current
        .Where(g => !string.IsNullOrWhiteSpace(g.RomPath) && File.Exists(g.RomPath))
        .ToList();

    var removed = current.Count - kept.Count;

    // Manifest.Games is init-only → create a new record instance
    var newManifest = manifest with { Games = kept };
    Json.Save(CorePaths.ManifestPath, newManifest);

    Console.WriteLine(removed == 0
        ? "No stale entries found."
        : $"Removed {removed} stale manifest entr{(removed == 1 ? "y" : "ies")}.");
    Environment.Exit((int)ExitCodes.Ok);
}

/// <summary>
/// Register an Explorer "Install ROM" verb for given extensions.
/// We always register with --apply; you choose default copy/move behavior and
/// an optional forced shortcut policy.
/// </summary>
static void RegisterContext(IEnumerable<string> rest)
{
    // Path to this CLI (what Explorer will call)
    string? exeMaybe = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exeMaybe))
        Fail(ExitCodes.Fatal, "Cannot determine CLI executable path.");
    string exePath = exeMaybe!;

    // --ext=.iso,.bin,...  (default set if omitted)
    var extArg = rest.FirstOrDefault(a => a.StartsWith("--ext=", StringComparison.OrdinalIgnoreCase))
              ?? "--ext=.iso,.bin,.cue,.sfc,.smc,.gba,.nds,.zip,.7z";

    var eq = extArg.IndexOf('=');
    var listStr = eq >= 0 ? extArg[(eq + 1)..] : extArg;
    var list = listStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (list.Length == 0) Fail(ExitCodes.UsageError, "No extensions provided.");

    // Optional mode flags (mutually exclusive). Default = --copy for safety.
    bool wantsMove = rest.Any(a => a.Equals("--move", StringComparison.OrdinalIgnoreCase));
    bool wantsCopy = rest.Any(a => a.Equals("--copy", StringComparison.OrdinalIgnoreCase));
    if (wantsMove && wantsCopy)
        Fail(ExitCodes.UsageError, "Choose either --move or --copy (default is --copy).");
    bool copyMode = !wantsMove;

    // Optional shortcut policy (mutually exclusive). Default = follow Settings.
    bool wantsShortcut = rest.Any(a =>
        a.Equals("--shortcut", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("--create-shortcut", StringComparison.OrdinalIgnoreCase));
    bool wantsNoShortcut = rest.Any(a => a.Equals("--no-shortcut", StringComparison.OrdinalIgnoreCase));
    if (wantsShortcut && wantsNoShortcut)
        Fail(ExitCodes.UsageError, "Choose either --shortcut or --no-shortcut (not both).");
    bool? shortcutMode = wantsShortcut ? true : wantsNoShortcut ? false : (bool?)null;

    int ok = 0, fail = 0;
    foreach (var ext in list)
    {
        if (RegistryService.RegisterInstallVerbForExtension(ext, exePath, out var err,
                copyMode: copyMode, shortcutMode: shortcutMode)) ok++;
        else { fail++; Console.Error.WriteLine(err); }
    }

    var modeStr = copyMode ? "--copy" : "--move";
    var shortcutStr = shortcutMode is null
        ? "follow-settings"
        : (shortcutMode.Value ? "--shortcut" : "--no-shortcut");

    Console.WriteLine($"Registered context menu ({modeStr}, {shortcutStr}) for {ok} extension(s). Failures: {fail}.");
    Environment.Exit(fail == 0 ? (int)ExitCodes.Ok : (int)ExitCodes.UsageError);
}

/// <summary>
/// Validate coherence between settings, emulators, and filetypes config.
/// Returns 0 on success; 4 (UsageError) if any issues are found.
/// </summary>
static void ValidateConfig()
{
    var settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
    var catalog = Json.Load<EmulatorCatalog>(CorePaths.EmulatorCatalogPath) ?? new EmulatorCatalog();
    var ft = Json.Load<FileTypes>(CorePaths.FiletypesPath) ?? new FileTypes();
    var ftIndex = new FileTypesIndex(ft);

    var problems = new List<string>();

    // 1) settings.registeredExtensions ⊆ filetypes.AllExtensions
    var registered = settings.RegisteredExtensions?.ToList() ?? [];
    foreach (var raw in registered)
    {
        var norm = FileTypesIndex.NormalizeExt(raw);
        if (string.IsNullOrWhiteSpace(norm)) continue;
        if (!ftIndex.AllExtensions.Contains(norm))
            problems.Add($"Registered extension '{raw}' not present in filetypes.json.");
    }

    // 2) Every console listed in filetypes.PerConsole has at least one emulator in the catalog
    foreach (var console in ft.PerConsole.Keys)
    {
        var any = catalog.Emulators.Any(e => e.Consoles.Contains(console, StringComparer.OrdinalIgnoreCase));
        if (!any)
            problems.Add($"Console '{console}' has no emulator in emulators.json.");
    }

    // 3) Each user default maps to a real emulator that supports that console
    if (settings.DefaultEmulatorPerConsole != null)
    {
        foreach (var kv in settings.DefaultEmulatorPerConsole)
        {
            var console = kv.Key;
            var emuId = kv.Value;

            var emu = catalog.Emulators.FirstOrDefault(e => e.Id.Equals(emuId, StringComparison.OrdinalIgnoreCase));
            if (emu is null)
            {
                problems.Add($"Default emulator '{emuId}' for console '{console}' not found in catalog.");
                continue;
            }
            if (!emu.Consoles.Contains(console, StringComparer.OrdinalIgnoreCase))
                problems.Add($"Default emulator '{emuId}' does not declare support for console '{console}'.");
        }
    }

    if (problems.Count == 0)
    {
        Console.WriteLine("Configuration looks good ✅");
        Environment.Exit((int)ExitCodes.Ok);
    }
    else
    {
        Console.Error.WriteLine("Configuration issues found:");
        foreach (var p in problems) Console.Error.WriteLine(" - " + p);
        Environment.Exit((int)ExitCodes.UsageError);
    }
}

/// <summary>
/// Remove the Explorer "Install ROM" verb for given extensions.
/// </summary>
static void UnregisterContext(IEnumerable<string> rest)
{
    var extArg = rest.FirstOrDefault(a => a.StartsWith("--ext=", StringComparison.OrdinalIgnoreCase))
              ?? "--ext=.iso,.bin,.cue,.sfc,.smc,.gba,.nds,.zip,.7z";

    var eq = extArg.IndexOf('=');
    var listStr = eq >= 0 ? extArg[(eq + 1)..] : extArg;
    var list = listStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (list.Length == 0) Fail(ExitCodes.UsageError, "No extensions provided.");

    int ok = 0, fail = 0;
    foreach (var ext in list)
    {
        if (RegistryService.UnregisterInstallVerbForExtension(ext, out var err)) ok++;
        else { fail++; Console.Error.WriteLine(err); }
    }

    Console.WriteLine($"Unregistered context menu for {ok} extension(s). Failures: {fail}.");
    Environment.Exit(fail == 0 ? (int)ExitCodes.Ok : (int)ExitCodes.UsageError);
}

#endregion

#region Utilities

/// <summary>
/// If settings.ShortcutsRoot is set, move a created .lnk there (creating the folder).
/// Returns the new path, or the original path if unchanged/failure.
/// </summary>
static string MoveShortcutToRootIfConfigured(string createdShortcutPath, Settings settings)
{
    try
    {
        var root = settings?.ShortcutsRoot;
        if (string.IsNullOrWhiteSpace(root)) return createdShortcutPath;

        // Expand %USERPROFILE% etc.
        var expanded = Environment.ExpandEnvironmentVariables(
            root.Replace("/", Path.DirectorySeparatorChar.ToString()));

        Directory.CreateDirectory(expanded);
        var newPath = Path.Combine(expanded, Path.GetFileName(createdShortcutPath));

        if (!string.Equals(createdShortcutPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(newPath)) File.Delete(newPath);
            File.Move(createdShortcutPath, newPath);
            return newPath;
        }
    }
    catch (Exception ex)
    {
        Logger.Exception("MoveShortcutToRootIfConfigured", ex);
        // Non-fatal: fall back to original location
    }
    return createdShortcutPath;
}

/// <summary>
/// Resolve reasons a plan needs user input, formatted for display.
/// </summary>
static string FormatNeedsPromptReasons(InstallPlan plan)
{
    if (plan?.Notes is { Length: > 0 }) return "- " + string.Join("\n- ", plan.Notes);
    return "- Console or emulator could not be auto-resolved.";
}

/// <summary>
/// Get value for an option that can appear as '--name value' or '--name=value'.
/// Returns null if not present.
/// </summary>
static string? GetOptValue(string[] args, string name)
{
    // form: --name=value
    var eq = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
    if (eq is not null)
    {
        var parts = eq.Split('=', 2);
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
            return parts[1].Trim();
    }

    // form: --name value
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var v = args[i + 1];
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
    }
    return null;
}

/// <summary>
/// Seed default JSONs (settings, emulators, filetypes) from /seed if missing.
/// Safe to call repeatedly. Logs missing seed files (non-fatal).
/// </summary>
static void SeedIfMissing()
{
    try
    {
        static void TrySeed(string name, string target)
        {
            if (File.Exists(target)) return;

            var local = Path.Combine("seed", name);
            if (File.Exists(local))
            {
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(local, target, true);
                Logger.Info($"Seeded {name} to {target}");
            }
            else
            {
                Logger.Warn($"Seed file missing: {local}");
            }
        }

        TrySeed("settings.json", CorePaths.SettingsPath);
        TrySeed("emulators.json", CorePaths.EmulatorCatalogPath);
        TrySeed("filetypes.json", CorePaths.FiletypesPath);
        TrySeed("version.json", Path.Combine(CorePaths.AppDataRoot, "version.json"));

    }
    catch (Exception ex)
    {
        Logger.Exception("Seeding error", ex); // Non-fatal
    }
}

/// <summary>
/// Print command usage reference.
/// </summary>
static void PrintHelp()
{
    Console.WriteLine("""
RomInstaller CLI

Usage:
  rom install "<path-to-rom>"                 # dry-run: prints InstallPlan JSON
  rom install "<path-to-rom>" --apply         # copy/move and register in manifest
        [--move|--copy] [--console <id>] [--emulator <id>] [--shortcut|--no-shortcut]
  rom list [--json] [--console <id>] [--emulator <id>] [--no-shortcut] # list installed games; filters and JSON output supported
  rom register-context --ext=.iso,.gba,...    # add right-click "Install ROM"
        [--move|--copy]                       # choose default operation for Explorer verb (default: --copy)
        [--shortcut|--no-shortcut]            # force desktop shortcut behavior (default: follow settings)
  rom unregister-context --ext=.iso,.gba,...  # remove right-click entry
  rom uninstall --key <manifestId>            # uninstall by manifest id (files to Recycle Bin by default)
        [--permanent] [--keep-files] [--remove-shortcut-only]
  rom uninstall --shortcut "<path>.lnk"       # extract key from shortcut and uninstall
        [--permanent] [--keep-files] [--remove-shortcut-only]
  rom register-shortcut-verb                  # adds "Uninstall ROM" to *.lnk context menu (current user)
  rom unregister-shortcut-verb                # removes the uninstall verb from *.lnk
  rom validate-config                         # verify settings/emulators/filetypes
  rom prune-manifest                          # remove manifest entries with missing ROM files
  rom version [--json]                        # show version and seed/config info

Exit codes:
  0 OK | 2 NeedsPrompt | 4 UsageError | 6 NotFound | 7 StartFailure | 8 LaunchError | 99 Fatal
""");

}

/// <summary>
/// Log an error, optionally show a dialog (for Explorer launches), then exit.
/// </summary>
static void Fail(ExitCodes code, string message)
{
    Console.Error.WriteLine(message);
    Logger.Error($"{code}: {message}");

    try
    {
        // Heuristic: when output isn't redirected, we are likely interactive → show a box.
        if (!Console.IsOutputRedirected)
            RomInstaller.Core.Services.UserNotify.ErrorBox("RomInstaller", message);
    }
    catch { /* never let UI notify crash the CLI */ }

    Environment.Exit((int)code);
}

/// <summary>
/// Try to locate the WPF UI exe alongside the CLI (dev) or one folder up (solution/bin layout).
/// Returns a full path or null. All decisions are logged.
/// </summary>
static string? ResolveUiExeOrNull()
{
    try
    {
        var cliExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(cliExe))
        {
            Logger.Warn("ResolveUiExeOrNull: Environment.ProcessPath was null/empty.");
            return null;
        }

        var baseDir = Path.GetDirectoryName(cliExe);
        if (string.IsNullOrEmpty(baseDir))
        {
            Logger.Warn("ResolveUiExeOrNull: baseDir from CLI exe path was null/empty.");
            return null;
        }

        // 1) Sibling project folder (common Debug/Release dev layout)
        var sibling = Path.Combine(baseDir.Replace("\\RomInstaller.CLI", "\\RomInstaller.UI"), "RomInstaller.UI.exe");
        Logger.Info($"UI resolve candidate #1: {sibling}");
        if (File.Exists(sibling)) return sibling;

        // 2) Parent solution-style layout
        var parent = Directory.GetParent(baseDir);
        if (parent is not null)
        {
            var alt = Path.Combine(parent.FullName, "RomInstaller.UI", "RomInstaller.UI.exe");
            Logger.Info($"UI resolve candidate #2: {alt}");
            if (File.Exists(alt)) return alt;
        }

        // 3) Optional: an installed location under %APPDATA%
        var appdataAlt = Path.Combine(CorePaths.AppDataRoot, "RomInstaller.UI.exe");
        Logger.Info($"UI resolve candidate #3: {appdataAlt}");
        if (File.Exists(appdataAlt)) return appdataAlt;

        Logger.Warn("ResolveUiExeOrNull: No UI exe found in known locations.");
        return null;
    }
    catch (Exception ex)
    {
        Logger.Exception("ResolveUiExeOrNull", ex);
        return null;
    }
}

/// <summary>
/// Launch the WPF Resolve UI with arguments; return true if Process.Start succeeded.
/// On failure, logs and (when interactive) shows an error box.
/// </summary>
static bool TryLaunchResolveUi(string sourcePath, string? consoleOverride, string? emulatorOverride)
{
    var uiExe = ResolveUiExeOrNull();
    if (string.IsNullOrEmpty(uiExe) || !File.Exists(uiExe))
    {
        var msg = "RomInstaller UI not found. Please build/run the RomInstaller.UI project or install the UI.";
        Logger.Warn(msg);
        if (!Console.IsOutputRedirected)
            RomInstaller.Core.Services.UserNotify.ErrorBox("RomInstaller", msg);
        return false;
    }

    // Basic UI arg contract now supports optional hints:
    //   --resolve "<path>" [--console "<id>"] [--emulator "<id>"]
    var parts = new List<string> { $"--resolve \"{sourcePath}\"" };

    // only add when present
    if (!string.IsNullOrWhiteSpace(consoleOverride))
        parts.Add($"--console \"{consoleOverride}\"");

    if (!string.IsNullOrWhiteSpace(emulatorOverride))
        parts.Add($"--emulator \"{emulatorOverride}\"");

    var argLine = string.Join(" ", parts);
    var cwd = Path.GetDirectoryName(uiExe) ?? Environment.CurrentDirectory;


    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = uiExe,
        Arguments = argLine,
        WorkingDirectory = cwd,
        UseShellExecute = false
    };

    Logger.Info($"Launching UI: \"{uiExe}\" {argLine} (cwd: {cwd})");

    try
    {
        var p = System.Diagnostics.Process.Start(psi);
        if (p is null)
        {
            var msg = "Failed to start RomInstaller UI process (Process.Start returned null).";
            Logger.Warn(msg);
            if (!Console.IsOutputRedirected)
                RomInstaller.Core.Services.UserNotify.ErrorBox("RomInstaller", msg);
            return false;
        }
        return true;
    }
    catch (Exception ex)
    {
        Logger.Exception("TryLaunchResolveUi", ex);
        if (!Console.IsOutputRedirected)
            RomInstaller.Core.Services.UserNotify.ErrorBox("RomInstaller", $"Could not launch UI: {ex.Message}");
        return false;
    }
}

/// <summary>Delete a directory if it exists and is empty. Best-effort (logs only).</summary>
static void TryDeleteDirIfEmpty(string? dir)
{
    try
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!Directory.Exists(dir)) return;
        if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
        Directory.Delete(dir, false);
    }
    catch (Exception ex)
    {
        Logger.Exception("TryDeleteDirIfEmpty", ex);
    }
}


#endregion
