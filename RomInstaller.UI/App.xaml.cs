using System;
using System.IO;
using System.Linq;
using System.Windows;
using RomInstaller.Core.Services;

namespace RomInstaller.UI
{
    /// <summary>
    /// Application entry. If launched with:
    ///   RomInstaller.UI.exe --resolve "<path-to-rom>"
    /// we boot directly into the Resolve window; otherwise we just exit for now.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Logger.Info($"UI argv: {string.Join(" ", e.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            ShutdownMode = ShutdownMode.OnMainWindowClose; // ✅ ensure app quits when main window closes

            // Accept either:
            //   --resolve "<path>"
            //   --resolve=<path>
            string? sourcePath = null;
            string? consoleHint = null;
            string? emulatorHint = null;


            if (e.Args.Length > 0)
            {
                // 1) --resolve=<path>
                var eq = e.Args.FirstOrDefault(a => a.StartsWith("--resolve=", StringComparison.OrdinalIgnoreCase));
                if (eq is not null)
                {
                    sourcePath = eq["--resolve=".Length..].Trim().Trim('"');
                }
                else
                {
                    // 2) --resolve "<path>"
                    for (int i = 0; i < e.Args.Length - 1; i++)
                    {
                        if (e.Args[i].Equals("--resolve", StringComparison.OrdinalIgnoreCase))
                        {
                            sourcePath = e.Args[i + 1].Trim().Trim('"');
                            break;
                        }
                    }
                }

                // --- NEW: optional hints (both --name=value and --name value forms) ---

                // console hint
                var cEq = e.Args.FirstOrDefault(a => a.StartsWith("--console=", StringComparison.OrdinalIgnoreCase));
                if (cEq is not null)
                {
                    consoleHint = cEq["--console=".Length..].Trim().Trim('"');
                }
                else
                {
                    for (int i = 0; i < e.Args.Length - 1; i++)
                    {
                        if (e.Args[i].Equals("--console", StringComparison.OrdinalIgnoreCase))
                        {
                            consoleHint = e.Args[i + 1].Trim().Trim('"');
                            break;
                        }
                    }
                }

                // emulator hint
                var eEq = e.Args.FirstOrDefault(a => a.StartsWith("--emulator=", StringComparison.OrdinalIgnoreCase));
                if (eEq is not null)
                {
                    emulatorHint = eEq["--emulator=".Length..].Trim().Trim('"');
                }
                else
                {
                    for (int i = 0; i < e.Args.Length - 1; i++)
                    {
                        if (e.Args[i].Equals("--emulator", StringComparison.OrdinalIgnoreCase))
                        {
                            emulatorHint = e.Args[i + 1].Trim().Trim('"');
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                var win = new ResolveWindow
                {
                    DataContext = new ViewModels.ResolveViewModel(sourcePath, consoleHint, emulatorHint) // NEW
                };
                win.Show();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(sourcePath))
                {
                    // ❌ Don’t pass a dummy owner window; just show a simple message.
                    Logger.Warn($"Resolve requested but source not found: {sourcePath}");
                    MessageBox.Show(
                        "The selected ROM file could not be found.",
                        "RomInstaller",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                // For now, no dashboard—just exit.
                Shutdown();
            }
        }
    }
}
