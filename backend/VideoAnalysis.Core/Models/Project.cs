namespace VideoAnalysis.Core.Models;

public sealed record Project(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Description = null,
    string? HomeTeamName = null,
    string? AwayTeamName = null,
    string ProjectFolderPath = "",
    bool IsBroadcastMode = false);
