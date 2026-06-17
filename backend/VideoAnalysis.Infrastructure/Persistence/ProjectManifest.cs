namespace VideoAnalysis.Infrastructure.Persistence;

internal sealed record ProjectManifest(
    Guid Id,
    string Name,
    string? Description,
    string? HomeTeamName,
    string? AwayTeamName,
    string ProjectFolderPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsBroadcastMode = false);
