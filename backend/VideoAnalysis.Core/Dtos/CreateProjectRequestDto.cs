namespace VideoAnalysis.Core.Dtos;

public sealed record CreateProjectRequestDto(
    string ProjectName,
    string SourceVideoPath,
    string? VideoTitle = null,
    string? Description = null,
    string? HomeTeamName = null,
    string? AwayTeamName = null,
    bool MoveVideoToProjectFolder = false,
    bool IsBroadcastMode = false);
