# Uninstall regression coverage

Run `dotnet run --project RomInstaller.Tests --configuration Release` with .NET 8.
The dependency-free executable exits nonzero on any failed assertion. It compiles
actual Core uninstall sources, excluding unrelated Windows COM shortcut creation.

Tests use isolated temporary manifests. Injected deletion operations exercise
failure/success ordering and mode forwarding for ROM, folder, and automatic
selection; keep-files, keep-shortcut, shortcut-only, case-insensitive IDs, both
shortcut candidates, ROM fallback, and empty-parent cleanup are also covered.
Real permanent deletion and deterministic failure run on all platforms. Windows
adds a locked-ROM failure and real Recycle Bin cleanup. The workflow runs all 25
cases on Windows; other platforms run 23 cases. Recycle-mode injected tests check
mode forwarding rather than emulating the Windows shell.
