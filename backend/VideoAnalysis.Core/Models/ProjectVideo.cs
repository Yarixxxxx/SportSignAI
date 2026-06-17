namespace VideoAnalysis.Core.Models;

public sealed record ProjectVideo(
    Guid Id,
    Guid ProjectId,
    string Title,
    string OriginalFileName,
    string StoredFilePath,
    DateTimeOffset ImportedAtUtc,
    string? ProxyFilePath = null);
