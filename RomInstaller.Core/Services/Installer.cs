using System;
using System.Collections.Generic;
using System.Diagnostics; // ← NEW: for ProcessStartInfo / Process
using System.IO;
using System.Linq;
using System.Text;
using RomInstaller.Core.Models;

namespace RomInstaller.Core.Services;

/// <summary>
/// Executes a planned install:
///   • Creates the console/game folder tree
///   • Copies or moves the primary ROM file
///   • Copies any sidecar files (e.g., BINs referenced by a CUE)
///   • Normalizes CUE file "FILE" entries to relative names
///   • Persists/updates the manifest
///
/// Design goals:
///   • Defensive: validate inputs, verify destinations, never overwrite silently
///   • Transactional where feasible: maintain a rollback stack to undo partial work
///   • Clear user messages: surface exact failure reason up to the CLI
/// </summary>
public class Installer
{
    private readonly Settings _settings;
    private readonly EmulatorCatalog _catalog;

    public Installer(Settings settings, EmulatorCatalog catalog)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Apply a plan to disk.
    /// - Returns the created GameEntry on success.
    /// - Throws on validation failures or fatal IO (caller maps to ExitCodes).
    ///
    /// Parameters:
    ///   plan  : Resolved InstallPlan from Planner
    ///   move  : true = move source; false = copy (preserve source)
    ///   userMessage (out): human-friendly action summary ("Copied to ...")
    ///
    /// Notes on safety:
    ///   • Duplicate detection happens up-front (filesystem + manifest)
    ///   • We never overwrite an existing file; UniquePath() is used during copy/move
    ///   • Best-effort rollback for any file operations performed before a failure
    /// </summary>
    public GameEntry Apply(InstallPlan plan, bool move, out string? userMessage)
    {
        userMessage = null;

        // ---------- Basic validation ----------
        if (string.IsNullOrWhiteSpace(plan.SourcePath) || !File.Exists(plan.SourcePath))
            throw new ResourceMissingException($"Source file missing: {plan.SourcePath}");
        if (string.IsNullOrWhiteSpace(plan.Console) || plan.Console == "unknown")
            throw new RomInstallerException("Console not resolved; cannot apply.");
        if (string.IsNullOrWhiteSpace(plan.EmulatorId))
            throw new RomInstallerException("Emulator not selected; cannot apply.");

        // Ensure destination directory exists (e.g., %EMULATION%/<console>/ROMs/<title>/)
        if (!CorePaths.TryEnsureDirectory(plan.DestinationGameFolder, out var dirErr))
            throw new RomInstallerException(dirErr!);

        // Rollback scaffolding (undo actions + temp files to delete)
        var ops = new List<Action>();
        var toDeleteOnRollback = new List<string>();

        // Compute final destination file path (we’ll still uniquify if needed)
        string finalRomPath = Path.Combine(
            plan.DestinationGameFolder,
            Path.GetFileName(plan.DestinationRomPath)
        );

        // Collect sidecars up-front (BINs for a CUE-based game)
        var sidecars = CollectSidecars(plan.SourcePath);

        try
        {
            // ---------- Preflight duplicate checks ----------
            // 1) Direct filesystem duplicate at planned destination (same filename)
            if (File.Exists(finalRomPath))
            {
                var msg = $"ROM already exists at destination: {finalRomPath}";
                Logger.Warn(msg);
                throw new RomInstallerException(msg);
            }

            // 2) Manifest duplicate handling with *auto-prune* of stale entries
            var manifestPre = Json.Load<Manifest>(CorePaths.ManifestPath) ?? new Manifest();

            var stale = manifestPre.Games
                .FirstOrDefault(g =>
                    !string.IsNullOrEmpty(g.RomPath) &&
                    string.Equals(g.RomPath, finalRomPath, StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(finalRomPath));

            if (stale is not null)
            {
                Logger.Warn($"Pruning stale manifest entry for missing ROM: {stale.Title} ({stale.Id})");
                manifestPre.Games.RemoveAll(g => g.Id == stale.Id);
                Json.Save(CorePaths.ManifestPath, manifestPre);
            }

            // 3) After pruning, if a manifest entry still references this path → hard dupe
            var stillDup = manifestPre.Games.Any(g =>
                !string.IsNullOrEmpty(g.RomPath) &&
                string.Equals(g.RomPath, finalRomPath, StringComparison.OrdinalIgnoreCase));

            if (stillDup)
            {
                var msg = $"Manifest already contains a ROM at destination: {finalRomPath}";
                Logger.Warn(msg);
                throw new RomInstallerException(msg);
            }

            // ---------- Stage 1: copy/move primary ROM ----------
            finalRomPath = CopyOrMove(plan.SourcePath, finalRomPath, move, ops, toDeleteOnRollback);

            // ---------- Stage 2: copy/move sidecars ----------
            foreach (var s in sidecars)
            {
                var dest = Path.Combine(plan.DestinationGameFolder, Path.GetFileName(s));

                // Never overwrite a pre-existing sidecar either (fail fast).
                if (File.Exists(dest))
                {
                    var msg = $"Sidecar already exists at destination: {dest}";
                    Logger.Warn(msg);
                    throw new RomInstallerException(msg);
                }
                CopyOrMove(s, dest, move, ops, toDeleteOnRollback);
            }

            // ---------- Stage 3: normalize CUE → relative FILE paths ----------
            if (string.Equals(Path.GetExtension(finalRomPath), ".cue", StringComparison.OrdinalIgnoreCase))
                RewriteCueToRelative(finalRomPath);

            // ---------- Stage 4: persist manifest ----------
            var manifest = manifestPre; // reuse already-loaded copy (possibly pruned)
            var entry = new GameEntry
            {
                Title = plan.Title,
                Console = plan.Console,
                EmulatorId = plan.EmulatorId,
                RomPath = finalRomPath,
                GameFolder = plan.DestinationGameFolder,
                InstalledAt = DateTime.UtcNow
            };

            manifest.Games.Add(entry);
            Json.Save(CorePaths.ManifestPath, manifest);

            // ---------- NEW: Stage 5 (non-blocking): auto-create a desktop shortcut ----------
            // We respect the user setting (default true). The actual work is offloaded to the CLI
            // via a *separate process* so this install flow stays responsive and robust.
            try
            {
                if (_settings.AutoCreateShortcut)
                {
                    // Environment.ProcessPath should be the CLI exe hosting this core call.
                    var cliExe = Environment.ProcessPath;

                    if (!string.IsNullOrWhiteSpace(cliExe) && File.Exists(cliExe))
                    {
                        var args = $"create-shortcut --key {entry.Id}";

                        var psi = new ProcessStartInfo
                        {
                            FileName = cliExe,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(cliExe) ?? Environment.CurrentDirectory
                        };

                        // Fire-and-forget; we don't wait or surface failures here.
                        _ = Process.Start(psi);
                        Logger.Info($"Auto-shortcut requested via CLI: {Path.GetFileName(cliExe)} {args}");
                    }
                    else
                    {
                        Logger.Warn("Auto-shortcut skipped: CLI exe not found via Environment.ProcessPath.");
                    }
                }
                else
                {
                    Logger.Info("Auto-shortcut disabled by settings (AutoCreateShortcut=false).");
                }
            }
            catch (Exception exShort)
            {
                // Absolutely non-fatal—install already succeeded.
                Logger.Exception("Auto-shortcut creation (post-install)", exShort);
            }

            // ---------- Done ----------
            userMessage = $"Installed '{entry.Title}' to {entry.GameFolder}";
            return entry;
        }
        catch (Exception ex)
        {
            // ---------- Rollback on failure ----------
            Logger.Exception("Installer.Apply", ex);

            foreach (var undo in ops.AsEnumerable().Reverse())
                TryDo(undo, "rollback-op");

            foreach (var file in toDeleteOnRollback.Distinct())
                TryDelete(file);

            throw; // Propagate to CLI — it maps to proper exit codes & user messages
        }
    }

    /// <summary>
    /// Copy or move a file from source → destPath (with UniquePath applied).
    /// Registers rollback actions so the operation can be undone if a later step fails.
    ///
    /// Returns: the *actual* destination path (may differ if uniquified).
    /// </summary>
    private static string CopyOrMove(
        string source,
        string dest,
        bool move,
        List<Action> rollback,
        List<string> deleteList)
    {
        // Don’t overwrite existing files; pick a unique variant if needed.
        var destPath = UniquePath(dest);

        if (move)
        {
            File.Move(source, destPath);
            // Rollback: move back to original location (best-effort)
            rollback.Add(() => TryMove(destPath, source));
        }
        else
        {
            File.Copy(source, destPath);
            // If we copied, it’s safe to delete this file on rollback
            deleteList.Add(destPath);
        }
        return destPath;
    }

    /// <summary>
    /// For a .cue primary source, gather co-located .bin files that are likely referenced by it.
    /// For non-.cue sources, returns an empty collection.
    /// </summary>
    private static IEnumerable<string> CollectSidecars(string source)
    {
        var ext = Path.GetExtension(source);
        if (!string.Equals(ext, ".cue", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Empty<string>();

        var dir = Path.GetDirectoryName(source)!;
        var bins = Directory.EnumerateFiles(dir, "*.bin", SearchOption.TopDirectoryOnly);
        return bins;
    }

    /// <summary>
    /// Rewrites CUE file "FILE" lines to use relative basenames only (no directories),
    /// and writes a .bak the first time as a safety net.
    ///
    /// Implementation notes:
    ///   • Minimal parsing: find FILE … "path" and replace with just the filename
    ///   • Writes ASCII to preserve classic CUE encoding
    ///   • Non-fatal on failure (emulator may still be able to load)
    /// </summary>
    private static void RewriteCueToRelative(string cuePath)
    {
        var backup = cuePath + ".bak";
        try
        {
            if (!File.Exists(backup))
                File.Copy(cuePath, backup);

            var lines = File.ReadAllLines(cuePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var idx = line.IndexOf("FILE", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Locate the first quoted path after FILE
                    var q1 = line.IndexOf('"', idx);
                    var q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1)
                    {
                        var path = line.Substring(q1 + 1, q2 - q1 - 1);
                        var baseName = Path.GetFileName(path);
                        lines[i] = line.Substring(0, q1 + 1) + baseName + line.Substring(q2);
                    }
                }
            }
            File.WriteAllLines(cuePath, lines, Encoding.ASCII);
        }
        catch (Exception ex)
        {
            Logger.Exception("RewriteCueToRelative", ex);
            // Intentionally non-fatal
        }
    }

    /// <summary>
    /// If the target path exists, returns "name (1).ext", then "(2)", … until free.
    /// Prevents accidental overwrites of user files.
    /// </summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        int i = 1;
        string candidate;
        do { candidate = Path.Combine(dir, $"{name} ({i++}){ext}"); }
        while (File.Exists(candidate));

        return candidate;
    }

    /// <summary>Best-effort delete: only logs on failure.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.Exception("TryDelete", ex); }
    }

    /// <summary>
    /// Best-effort move: ensures target directory exists, replaces existing file,
    /// and logs on failure (no exceptions on purpose for rollback flow).
    /// </summary>
    private static void TryMove(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
        catch (Exception ex) { Logger.Exception("TryMove", ex); }
    }

    /// <summary>
    /// Executes a rollback action, logging any exception but not throwing.
    /// </summary>
    private static void TryDo(Action act, string ctx)
    {
        try { act(); }
        catch (Exception ex) { Logger.Exception(ctx, ex); }
    }
}
