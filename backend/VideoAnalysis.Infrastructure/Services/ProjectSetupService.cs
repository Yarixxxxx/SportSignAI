using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Dtos;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Infrastructure.Services;

public sealed class ProjectSetupService : IProjectSetupService
{
    private readonly IProjectRepository _repository;
    private readonly string _projectsRootPath;

    public ProjectSetupService(IProjectRepository repository, string projectsRootPath)
    {
        _repository = repository;
        _projectsRootPath = projectsRootPath;
        Directory.CreateDirectory(_projectsRootPath);
    }

    public async Task<CreateProjectResultDto> CreateProjectWithVideoAsync(
        CreateProjectRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
        {
            throw new ArgumentException("Project name is required.", nameof(request));
        }

        var hasSourceVideo = !string.IsNullOrWhiteSpace(request.SourceVideoPath);
        if (!request.IsBroadcastMode && !hasSourceVideo)
        {
            throw new ArgumentException("Source video path is required.", nameof(request));
        }

        var sourceVideoPath = hasSourceVideo ? Path.GetFullPath(request.SourceVideoPath) : string.Empty;
        if (hasSourceVideo && !File.Exists(sourceVideoPath))
        {
            throw new FileNotFoundException("Source video file was not found.", sourceVideoPath);
        }

        await _repository.InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var projectFolderName = $"{SanitizeForPath(request.ProjectName)}-{projectId:N}";
        var projectFolderPath = Path.Combine(_projectsRootPath, projectFolderName);
        var mediaFolderPath = Path.Combine(projectFolderPath, "media");
        var exportsFolderPath = Path.Combine(projectFolderPath, "exports");
        Directory.CreateDirectory(projectFolderPath);
        Directory.CreateDirectory(mediaFolderPath);
        Directory.CreateDirectory(exportsFolderPath);

        var project = new Project(
            projectId,
            request.ProjectName.Trim(),
            now,
            now,
            Normalize(request.Description),
            Normalize(request.HomeTeamName),
            Normalize(request.AwayTeamName),
            projectFolderPath,
            request.IsBroadcastMode);

        await _repository.CreateProjectAsync(project, cancellationToken);
        ProjectVideo? projectVideo = null;
        if (hasSourceVideo)
        {
            var originalFileName = Path.GetFileName(sourceVideoPath);
            var storedFileName = GetAvailableFileName(mediaFolderPath, originalFileName);
            var storedVideoPath = Path.Combine(mediaFolderPath, storedFileName);

            File.Copy(sourceVideoPath, storedVideoPath, overwrite: false);

            projectVideo = new ProjectVideo(
                Guid.NewGuid(),
                projectId,
                Normalize(request.VideoTitle) ?? Path.GetFileNameWithoutExtension(originalFileName),
                originalFileName,
                storedVideoPath,
                now);

            await _repository.UpsertProjectVideoAsync(projectVideo, cancellationToken);
        }

        return new CreateProjectResultDto(
            project.Id,
            project.ProjectFolderPath,
            projectVideo?.StoredFilePath ?? string.Empty,
            projectVideo?.Title ?? string.Empty);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string SanitizeForPath(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select((character) => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "project" : sanitized;
    }

    private static string GetAvailableFileName(string folderPath, string originalFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        var extension = Path.GetExtension(originalFileName);
        var sanitizedBaseName = SanitizeForPath(baseName);
        var candidate = $"{sanitizedBaseName}{extension}";
        var counter = 1;

        while (File.Exists(Path.Combine(folderPath, candidate)))
        {
            candidate = $"{sanitizedBaseName}-{counter}{extension}";
            counter++;
        }

        return candidate;
    }
}
