using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using RomInstaller.Core;
using RomInstaller.Core.Enums;
using RomInstaller.Core.Models;
using RomInstaller.Core.Services;

namespace RomInstaller.UI.ViewModels
{
    /// <summary>
    /// ViewModel that backs the Resolve window:
    ///  • shows ROM path + title
    ///  • lets user choose Console + Emulator
    ///  • runs CLI "rom install <path> --apply --console X --emulator Y"
    /// </summary>
    public sealed class ResolveViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly Settings _settings;
        private readonly EmulatorCatalog _catalog;
        private readonly FileTypesIndex _ftIndex;

        public string SourcePath { get; }
        public string Title { get; }

        // UI options (C# 12 collection expressions)
        public ObservableCollection<string> ConsoleDetails => ConsoleOptions; // optional alias if you bind elsewhere
        public ObservableCollection<string> ConsoleOptions { get; } = [];
        public ObservableCollection<EmuOption> EmulatorOptions { get; } = [];

        // Selections
        private string? _selectedConsole;
        public string? SelectedConsole
        {
            get => _selectedConsole;
            set
            {
                if (Set(ref _selectedConsole, value))
                {
                    // Rebuild emulator candidates whenever console changes
                    RebuildEmulatorOptions();
                }
            }
        }

        private EmuOption? _selectedEmulator;
        public EmuOption? SelectedEmulator
        {
            get => _selectedEmulator;
            set => Set(ref _selectedEmulator, value);
        }

        // Commands
        public ICommand InstallCommand { get; }

        public ResolveViewModel(string sourcePath, string? prefConsoleId = null, string? prefEmuId = null)
        {
            SourcePath = sourcePath;
            Title = ConsoleDetector.SanitizeTitle(Path.GetFileName(sourcePath));

            // Load configs (safe-load with defaults)
            _settings = Json.Load<Settings>(CorePaths.SettingsPath) ?? new Settings();
            _catalog = Json.Load<EmulatorCatalog>(CorePaths.EmulatorCatalogPath) ?? new EmulatorCatalog();
            var ft = Json.Load<FileTypes>(CorePaths.FiletypesPath) ?? new FileTypes();
            _ftIndex = new FileTypesIndex(ft);

            // Precompute plan only to propose initial picks
            var planner = new Planner(_settings, _catalog, _ftIndex);
            var plan = planner.PlanFromFile(sourcePath);

            // ----- Populate console options -----
            ConsoleOptions.Clear();

            // Helper: all known consoles from catalog
            var allConsoles = _catalog.Emulators
                .SelectMany(e => e.Consoles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();

            // Prefer explicit hint > detected plan > extension guess > first known
            string? initialConsole = null;

            if (!string.IsNullOrWhiteSpace(prefConsoleId))
            {
                initialConsole = prefConsoleId;
            }
            else if (!string.IsNullOrWhiteSpace(plan.Console) && !plan.Console.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                initialConsole = plan.Console;
            }
            else
            {
                var byExt = GuessConsoleByExt(sourcePath);
                initialConsole = allConsoles.Contains(byExt, StringComparer.OrdinalIgnoreCase) ? byExt : allConsoles.FirstOrDefault();
            }

            // If emulator hint is present but console isn't, infer console from emulator map (first supported)
            if (string.IsNullOrWhiteSpace(initialConsole) && !string.IsNullOrWhiteSpace(prefEmuId))
            {
                var em = _catalog.Emulators.FirstOrDefault(e => e.Id.Equals(prefEmuId, StringComparison.OrdinalIgnoreCase));
                if (em is not null && em.Consoles.Length > 0) initialConsole = em.Consoles[0];
            }

            // Build ConsoleOptions with preferred first, then rest
            if (!string.IsNullOrWhiteSpace(initialConsole))
            {
                ConsoleOptions.Add(initialConsole);
                foreach (var c in allConsoles.Where(c => !c.Equals(initialConsole, StringComparison.OrdinalIgnoreCase)))
                    ConsoleOptions.Add(c);
                SelectedConsole = initialConsole; // triggers RebuildEmulatorOptions()
            }
            else
            {
                foreach (var c in allConsoles) ConsoleOptions.Add(c);
                SelectedConsole = ConsoleOptions.FirstOrDefault();
            }

            // ----- Emulators depend on SelectedConsole -----
            RebuildEmulatorOptions();

            // Prefer explicit emulator hint > plan emulator (if compatible)
            if (!string.IsNullOrWhiteSpace(prefEmuId))
            {
                var match = EmulatorOptions.FirstOrDefault(e => e.Id.Equals(prefEmuId, StringComparison.OrdinalIgnoreCase));
                if (match is not null) SelectedEmulator = match;
            }
            else if (!string.IsNullOrWhiteSpace(plan.EmulatorId))
            {
                var match = EmulatorOptions.FirstOrDefault(e => e.Id.Equals(plan.EmulatorId, StringComparison.OrdinalIgnoreCase));
                if (match is not null) SelectedEmulator = match;
            }

            InstallCommand = new RelayCommand(_ => DoInstall(), _ => CanInstall());
        }

        private bool CanInstall()
            => File.Exists(SourcePath)
               && !string.IsNullOrWhiteSpace(SelectedConsole)
               && SelectedEmulator is not null;

        private void DoInstall()
        {
            try
            {
                var cliExe = ResolveCliExeOrNull();
                if (string.IsNullOrEmpty(cliExe) || !File.Exists(cliExe))
                {
                    MessageBox.Show("Could not find RomInstaller.CLI executable. Please build the CLI project.", "RomInstaller", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Build CLI arguments:
                //   rom install "<path>" --apply --copy (safest default)
                //   --console <id> --emulator <id>
                var args = $"install \"{SourcePath}\" --apply --copy --console \"{SelectedConsole}\" --emulator \"{SelectedEmulator!.Id}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = cliExe,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(cliExe) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var p = Process.Start(psi);
                if (p is null)
                {
                    MessageBox.Show("Failed to launch RomInstaller CLI.", "RomInstaller", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (p.ExitCode == (int)ExitCodes.Ok)
                {
                    MessageBox.Show(string.IsNullOrWhiteSpace(stdout) ? "Installed." : stdout, "RomInstaller", MessageBoxButton.OK, MessageBoxImage.Information);
                    // ✅ Close the main window (App.ShutdownMode closes the app too)
                    Application.Current.MainWindow?.Close();
                }
                else if (p.ExitCode == (int)ExitCodes.NeedsPrompt)
                {
                    MessageBox.Show("More information required. Please set defaults or try again with different choices.", "RomInstaller", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    var msg = !string.IsNullOrWhiteSpace(stderr) ? stderr : "Install failed. See logs for details.";
                    MessageBox.Show(msg, "RomInstaller", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception("ResolveViewModel.DoInstall", ex);
                MessageBox.Show($"Unexpected error: {ex.Message}", "RomInstaller", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RebuildEmulatorOptions()
        {
            EmulatorOptions.Clear();
            if (string.IsNullOrWhiteSpace(SelectedConsole)) return;

            // Emulators supporting this console
            var list = _catalog.Emulators
                               .Where(e => e.Consoles.Contains(SelectedConsole, StringComparer.OrdinalIgnoreCase))
                               .Select(e => new EmuOption(e.Id, e.Name))
                               .OrderBy(e => e.Name)
                               .ToList();

            foreach (var e in list) { EmulatorOptions.Add(e); }

            // Prefer user default for this console (if any)
            var defaultId = _settings.DefaultEmulatorPerConsole != null &&
                            _settings.DefaultEmulatorPerConsole.TryGetValue(SelectedConsole, out var d)
                                ? d
                                : null;

            if (!string.IsNullOrWhiteSpace(defaultId))
            {
                var match = EmulatorOptions.FirstOrDefault(o => o.Id.Equals(defaultId, StringComparison.OrdinalIgnoreCase));
                if (match is not null) SelectedEmulator = match;
            }

            // Otherwise pick the first available
            SelectedEmulator ??= EmulatorOptions.FirstOrDefault();
        }

        private static string GuessConsoleByExt(string path)
        {
            var ext = FileTypesIndex.GetPathExtNoDot(path);
            return ext switch
            {
                "cue" or "bin" => "ps1",
                "iso" => "ps2",
                "sfc" or "smc" => "snes",
                "gba" => "gba",
                "nds" => "nds",
                "wbfs" => "wii",
                "rvz" or "gcm" => "gamecube",
                "cso" => "psp",
                _ => "unknown"
            };
        }

        private static string? ResolveCliExeOrNull()
        {
            try
            {
                // Prefer sibling folder in dev builds
                var uiExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(uiExe)) return null;

                var baseDir = Path.GetDirectoryName(uiExe);
                if (string.IsNullOrEmpty(baseDir)) return null;

                var sibling = Path.Combine(baseDir.Replace("\\RomInstaller.UI", "\\RomInstaller.CLI"), "RomInstaller.CLI.exe");
                Logger.Info($"CLI resolve candidate #1: {sibling}");
                if (File.Exists(sibling)) return sibling;

                // Parent solution-style layout
                var parent = Directory.GetParent(baseDir);
                if (parent is not null)
                {
                    var alt = Path.Combine(parent.FullName, "RomInstaller.CLI", "RomInstaller.CLI.exe");
                    Logger.Info($"CLI resolve candidate #2: {alt}");
                    if (File.Exists(alt)) return alt;
                }

                // Optional: installed location under %APPDATA%
                var appdataAlt = Path.Combine(CorePaths.AppDataRoot, "RomInstaller.CLI.exe");
                Logger.Info($"CLI resolve candidate #3: {appdataAlt}");
                if (File.Exists(appdataAlt)) return appdataAlt;

                Logger.Warn("ResolveCliExeOrNull: No CLI exe found in known locations.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Exception("ResolveCliExeOrNull", ex);
                return null;
            }
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value!;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            CommandManager.InvalidateRequerySuggested(); // to force requery of CanExecute
            return true;
        }

        public sealed record EmuOption(string Id, string Name)
        {
            public override string ToString() => Name;
        }
    }

    /// <summary>
    /// Minimal ICommand helper using a C# 12 primary constructor.
    /// Keeps analyzers happy (“Use primary constructor”) and avoids boilerplate.
    /// </summary>
    public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
    {
        public bool CanExecute(object? parameter) => can?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => run(parameter);
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
