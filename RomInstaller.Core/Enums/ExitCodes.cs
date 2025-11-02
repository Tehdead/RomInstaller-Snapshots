namespace RomInstaller.Core.Enums;

/// <summary>
/// Stable process exit codes for CLI & Launcher.
/// Keep values stable so scripts and logs stay meaningful.
/// </summary>
public enum ExitCodes
{
    Ok = 0,
    NeedsPrompt = 2,          // Ambiguous detection; user input required
    UsageError = 4,           // Bad args, missing inputs, invalid options
    NotFound = 6,             // File/ROM/emulator missing
    StartFailure = 7,         // Could not start external process
    LaunchError = 8,          // Emulator launch threw an exception
    Fatal = 99                // Unhandled fatal error (should be rare)
}
