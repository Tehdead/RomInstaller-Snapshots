namespace RomInstaller.Core.Models;

/// <summary>Pure planning result (no writes), printed by CLI --dry-run.</summary>
public record InstallPlan
{
    public string SourcePath { get; init; } = "";
    public string? StagedCandidate { get; init; }
    public string Console { get; init; } = "";
    public string Title { get; init; } = "";
    public string EmulatorId { get; init; } = "";
    public string DestinationRomPath { get; init; } = "";
    public string DestinationGameFolder { get; init; } = "";
    public bool NeedsPrompt { get; init; }
    public string[] Notes { get; init; } = System.Array.Empty<string>();
}
