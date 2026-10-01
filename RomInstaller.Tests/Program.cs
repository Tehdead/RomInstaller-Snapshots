using RomInstaller.Core;
using RomInstaller.Core.Models;
using RomInstaller.Core.Services;
using Mode = RomInstaller.Core.Services.FileDeleteService.DeletionMode;
using Target = RomInstaller.Core.Services.Uninstaller.DeleteTarget;

// Dependency-free regression runner: dotnet run --project RomInstaller.Tests
var passed = 0;
foreach (var mode in Enum.GetValues<Mode>())
foreach (var target in new[] { Target.RomFile, Target.GameFolder, Target.Auto })
{
    Run($"{target} failure retains manifest and shortcuts ({mode})", f =>
    {
        var before = File.ReadAllText(f.ManifestPath);
        var uninstaller = f.Create(mode, fail: true);
        try
        {
            uninstaller.UninstallByKey("GAME", target, mode, false, shortcutPath: f.ExtraShortcut);
            throw new Exception("Expected deletion failure");
        }
        catch (RomInstallerException ex)
        {
            Check(ex.Message.Contains("Manifest entry retained"), "failure diagnostic");
        }
        Check(File.ReadAllText(f.ManifestPath) == before, "manifest unchanged byte-for-byte");
        Check(File.Exists(f.Rom) && File.Exists(f.Shortcut) && File.Exists(f.ExtraShortcut), "files and shortcuts retained");
        Check(f.Calls.Count == 1, "stop immediately after failed game deletion");
    });
    Run($"{target} success cleans files, shortcuts and only requested entry ({mode})", f =>
    {
        var summary = f.Create(mode).UninstallByKey("GAME", target, mode, false, shortcutPath: f.ExtraShortcut);
        Check(!File.Exists(f.Rom) && !Directory.Exists(f.Folder), "game and empty parent removed");
        Check(!File.Exists(f.Shortcut) && !File.Exists(f.ExtraShortcut), "both shortcuts removed");
        Check(Json.Load<Manifest>(f.ManifestPath)!.Games.Single().Id == "other", "unrelated entry retained");
        Check(summary.Contains(mode == Mode.Permanent ? "permanently deleted" : "Recycle Bin"), "correct mode summary");
    });
    Run($"{target} shortcut-only bypasses game deletion and keeps manifest ({mode})", f =>
    {
        var before = File.ReadAllText(f.ManifestPath);
        f.Create(mode).UninstallByKey("game", target, mode, false, shortcutOnly: true, shortcutPath: f.ExtraShortcut);
        Check(File.Exists(f.Rom) && File.ReadAllText(f.ManifestPath) == before, "game and manifest retained");
        Check(!File.Exists(f.Shortcut) && !File.Exists(f.ExtraShortcut), "shortcuts removed");
        Check(f.Calls.All(p => p.EndsWith(".lnk")), "only shortcuts deleted");
    });
}
Run("keep-files removes entry and shortcuts", f =>
{
    f.Create(Mode.Permanent).UninstallByKey("game", Target.None, Mode.Permanent, false);
    Check(File.Exists(f.Rom) && !File.Exists(f.Shortcut), "keep-files behavior");
    Check(Json.Load<Manifest>(f.ManifestPath)!.Games.Single().Id == "other", "entry removed");
});
Run("keepShortcut preserves shortcut", f =>
{
    f.Create(Mode.Permanent).UninstallByKey("game", Target.GameFolder, Mode.Permanent, true);
    Check(File.Exists(f.Shortcut), "shortcut retained");
});
Run("Auto falls back to ROM when folder is absent", f =>
{
    var manifest = Json.Load<Manifest>(f.ManifestPath)!;
    Json.Save(f.ManifestPath, manifest with { Games = manifest.Games.Select(g => g.Id == "game" ? g with { GameFolder = "" } : g).ToList() });
    f.Create(Mode.Permanent).UninstallByKey("game", Target.Auto, Mode.Permanent, false);
    Check(f.Calls[0] == f.Rom && !Directory.Exists(f.Folder), "ROM fallback and empty parent cleanup");
});
Run("real permanent-delete service removes installed folder", f =>
{
    new Uninstaller(f.ManifestPath, FileDeleteService.TryDeleteFile, FileDeleteService.TryDeleteDirectory)
        .UninstallByKey("game", Target.GameFolder, Mode.Permanent, false);
    Check(!Directory.Exists(f.Folder) && !File.Exists(f.Shortcut), "real filesystem cleanup");
    Check(Json.Load<Manifest>(f.ManifestPath)!.Games.Single().Id == "other", "manifest cleanup");
});
Run("real deletion failure retains manifest", f =>
{
    // A file where a directory was requested deterministically fails on every OS.
    var manifest = Json.Load<Manifest>(f.ManifestPath)!;
    Json.Save(f.ManifestPath, manifest with { Games = manifest.Games.Select(g => g.Id == "game" ? g with { GameFolder = f.Rom } : g).ToList() });
    var before = File.ReadAllText(f.ManifestPath);
    try
    {
        new Uninstaller(f.ManifestPath, FileDeleteService.TryDeleteFile, FileDeleteService.TryDeleteDirectory)
            .UninstallByKey("game", Target.GameFolder, Mode.Permanent, false);
        throw new Exception("Expected failure");
    }
    catch (RomInstallerException) { }
    Check(File.ReadAllText(f.ManifestPath) == before && File.Exists(f.Shortcut), "failure retains registration");
});
if (OperatingSystem.IsWindows())
{
    Run("locked ROM deletion failure retains manifest and shortcut", f =>
    {
        var before = File.ReadAllText(f.ManifestPath);
        using var locked = new FileStream(f.Rom, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            new Uninstaller(f.ManifestPath, FileDeleteService.TryDeleteFile, FileDeleteService.TryDeleteDirectory)
                .UninstallByKey("game", Target.RomFile, Mode.Permanent, false);
            throw new Exception("Expected locked file failure");
        }
        catch (RomInstallerException) { }
        Check(File.ReadAllText(f.ManifestPath) == before && File.Exists(f.Shortcut), "locked file retains registration");
    });
    Run("real recycle-bin cleanup", f =>
    {
        new Uninstaller(f.ManifestPath, FileDeleteService.TryDeleteFile, FileDeleteService.TryDeleteDirectory)
            .UninstallByKey("game", Target.GameFolder, Mode.RecycleBin, false);
        Check(!Directory.Exists(f.Folder) && !File.Exists(f.Shortcut), "recycled assets removed from installation");
        Check(Json.Load<Manifest>(f.ManifestPath)!.Games.Single().Id == "other", "manifest cleanup after recycling");
    });
}
Console.WriteLine($"Passed {passed} uninstall regression cases.");

void Run(string name, Action<Fixture> test)
{
    using var fixture = new Fixture();
    test(fixture);
    passed++;
    Console.WriteLine($"PASS: {name}");
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
sealed class Fixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RomInstallerTests-" + Guid.NewGuid().ToString("N"));
    public string ManifestPath => Path.Combine(root, "manifest.json");
    public string Folder => Path.Combine(root, "game");
    public string Rom => Path.Combine(Folder, "game.rom");
    public string Shortcut => Path.Combine(root, "game.lnk");
    public string ExtraShortcut => Path.Combine(root, "extra.lnk");
    public List<string> Calls { get; } = [];
    public Fixture()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Rom, "rom");
        File.WriteAllText(Shortcut, "shortcut");
        File.WriteAllText(ExtraShortcut, "shortcut");
        Json.Save(ManifestPath, new Manifest { Games = [
            new GameEntry { Id = "game", Title = "Game", GameFolder = Folder, RomPath = Rom, ShortcutPath = Shortcut },
            new GameEntry { Id = "other", Title = "Unrelated" }
        ] });
    }
    public Uninstaller Create(Mode expectedMode, bool fail = false)
    {
        currentMode = expectedMode;
        FailDeletion = fail;
        return new(ManifestPath, DeleteFile, DeleteDirectory);
    }
    private Mode? currentMode;
    private bool FailDeletion;
    private bool DeleteFile(string path, Mode mode, out string? error) => Delete(path, mode, false, out error);
    private bool DeleteDirectory(string path, Mode mode, out string? error) => Delete(path, mode, true, out error);
    private bool Delete(string path, Mode mode, bool directory, out string? error)
    {
        if (currentMode != mode) throw new Exception("Deletion mode was not forwarded");
        Calls.Add(path);
        error = FailDeletion ? "Simulated access denied" : null;
        if (FailDeletion) return false;
        if (directory) Directory.Delete(path, true); else File.Delete(path);
        return true;
    }
    public void Dispose() => Directory.Delete(root, true);
}
