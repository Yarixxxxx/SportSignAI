using Microsoft.Data.Sqlite;
using VideoAnalysis.Core.Enums;
using VideoAnalysis.Core.Models;
using VideoAnalysis.Infrastructure.Persistence;
using VideoAnalysis.Infrastructure.Services;
using VideoAnalysis.Core.Dtos;

namespace VideoAnalysis.Tests;

public sealed class SqliteProjectRepositoryTests : IDisposable
{
    private readonly string _storageRootPath;

    public SqliteProjectRepositoryTests()
    {
        _storageRootPath = Path.Combine(Path.GetTempPath(), "video-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRootPath);
    }

    [Fact]
    public async Task CreateAndLoadProject_Works()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var project = new Project(
            Guid.NewGuid(),
            "Test Match",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Quarter-final",
            "Avto",
            "Ak Bars",
            Path.Combine(_storageRootPath, "test-match"));

        await repository.CreateProjectAsync(project, CancellationToken.None);

        var loaded = await repository.GetProjectAsync(project.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(project.Name, loaded!.Name);
        Assert.Equal(project.Description, loaded.Description);
        Assert.Equal(project.HomeTeamName, loaded.HomeTeamName);
        Assert.Equal(project.AwayTeamName, loaded.AwayTeamName);
        Assert.Equal(project.ProjectFolderPath, loaded.ProjectFolderPath);
        Assert.False(loaded.IsBroadcastMode);
    }

    [Fact]
    public async Task CreateAndLoadBroadcastProject_Works()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var project = new Project(
            Guid.NewGuid(),
            "Live Match",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Live tagging",
            "Home",
            "Away",
            Path.Combine(_storageRootPath, "live-match"),
            IsBroadcastMode: true);

        await repository.CreateProjectAsync(project, CancellationToken.None);

        var loaded = await repository.GetProjectAsync(project.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsBroadcastMode);
        Assert.Equal(project.Name, loaded.Name);
    }

    [Fact]
    public async Task UpsertAndLoadProjectVideo_Works()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var projectId = Guid.NewGuid();
        await repository.CreateProjectAsync(
            new Project(projectId, "Match", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, Path.Combine(_storageRootPath, "match")),
            CancellationToken.None);

        var video = new ProjectVideo(
            Guid.NewGuid(),
            projectId,
            "Game 1",
            "game1.mp4",
            Path.Combine(_storageRootPath, "match", "game1.mp4"),
            DateTimeOffset.UtcNow,
            Path.Combine(_storageRootPath, "match", "proxy", "game1.proxy.mp4"));

        await repository.UpsertProjectVideoAsync(video, CancellationToken.None);

        var loaded = await repository.GetProjectVideoAsync(projectId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(video.Title, loaded!.Title);
        Assert.Equal(video.StoredFilePath, loaded.StoredFilePath);
        Assert.Equal(video.ProxyFilePath, loaded.ProxyFilePath);
    }

    [Fact]
    public async Task CreateProjectWithVideo_MovesVideoIntoProjectFolder()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        var sourceFolder = Path.Combine(Path.GetTempPath(), "video-analysis-tests", "source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceFolder);

        var sourceVideoPath = Path.Combine(sourceFolder, "match.mp4");
        await File.WriteAllTextAsync(sourceVideoPath, "video", CancellationToken.None);

        try
        {
            var service = new ProjectSetupService(repository, _storageRootPath);
            var result = await service.CreateProjectWithVideoAsync(
                new CreateProjectRequestDto(
                    "Playoffs",
                    sourceVideoPath,
                    Description: "Round 1",
                    HomeTeamName: "Home",
                    AwayTeamName: "Away"),
                CancellationToken.None);

            Assert.True(File.Exists(sourceVideoPath));
            Assert.True(File.Exists(result.StoredVideoPath));
            Assert.Contains($"{Path.DirectorySeparatorChar}media{Path.DirectorySeparatorChar}", result.StoredVideoPath);

            var project = await repository.GetProjectAsync(result.ProjectId, CancellationToken.None);
            var video = await repository.GetProjectVideoAsync(result.ProjectId, CancellationToken.None);

            Assert.NotNull(project);
            Assert.NotNull(video);
            Assert.Equal("Home", project!.HomeTeamName);
            Assert.Equal("Away", project.AwayTeamName);
            Assert.Equal("match.mp4", video!.OriginalFileName);
            Assert.StartsWith(_storageRootPath, result.ProjectFolderPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(sourceFolder))
            {
                Directory.Delete(sourceFolder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateBroadcastProject_AllowsMissingVideo()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        var service = new ProjectSetupService(repository, _storageRootPath);

        var result = await service.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto(
                "Live Playoffs",
                string.Empty,
                Description: "Live mode",
                IsBroadcastMode: true),
            CancellationToken.None);

        var project = await repository.GetProjectAsync(result.ProjectId, CancellationToken.None);
        var video = await repository.GetProjectVideoAsync(result.ProjectId, CancellationToken.None);

        Assert.NotNull(project);
        Assert.True(project!.IsBroadcastMode);
        Assert.Null(video);
        Assert.Equal(string.Empty, result.StoredVideoPath);
    }

    [Fact]
    public async Task UpsertAndQueryEventTypesAndEvents_Works()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var projectId = Guid.NewGuid();
        await repository.CreateProjectAsync(
            new Project(projectId, "Events", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "Home", "Away", Path.Combine(_storageRootPath, "events")),
            CancellationToken.None);

        var preset = new TagPreset(Guid.NewGuid(), projectId, "Goal", "#E53935", "GameEvent", false, "G", "goal");
        await repository.UpsertTagPresetAsync(preset, CancellationToken.None);

        var tagEvent = new TagEvent(
            Guid.NewGuid(),
            projectId,
            preset.Id,
            200,
            260,
            null,
            null,
            "transition",
            DateTimeOffset.UtcNow,
            TeamSide.Home,
            false);
        await repository.UpsertTagEventAsync(tagEvent, CancellationToken.None);

        var loadedPresets = await repository.GetTagPresetsAsync(projectId, CancellationToken.None);
        Assert.Single(loadedPresets);
        Assert.Equal("G", loadedPresets[0].Hotkey);
        Assert.Equal("goal", loadedPresets[0].IconKey);

        var loadedEvents = await repository.GetTagEventsAsync(
            projectId,
            new TagQuery(preset.Id, null, null, null, TeamSide.Home, false),
            CancellationToken.None);

        Assert.Single(loadedEvents);
        Assert.Equal(200, loadedEvents[0].StartFrame);
        Assert.Equal(260, loadedEvents[0].EndFrame);
        Assert.Equal(TeamSide.Home, loadedEvents[0].TeamSide);
        Assert.False(loadedEvents[0].IsOpen);
    }

    [Fact]
    public async Task EventTypeHotkey_MustBeUniqueWithinProject()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var projectId = Guid.NewGuid();
        await repository.CreateProjectAsync(
            new Project(projectId, "Unique", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, Path.Combine(_storageRootPath, "unique")),
            CancellationToken.None);

        await repository.UpsertTagPresetAsync(
            new TagPreset(Guid.NewGuid(), projectId, "Goal", "#E53935", "GameEvent", false, "G", "goal"),
            CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.UpsertTagPresetAsync(
                new TagPreset(Guid.NewGuid(), projectId, "Save", "#1E88E5", "GameEvent", false, "g", "save"),
                CancellationToken.None));
    }

    [Fact]
    public async Task RegisterHotkeyPress_StartsAndStopsSingleEvent()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var projectId = Guid.NewGuid();
        await repository.CreateProjectAsync(
            new Project(projectId, "Capture", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, Path.Combine(_storageRootPath, "capture")),
            CancellationToken.None);

        var preset = new TagPreset(Guid.NewGuid(), projectId, "Goal", "#E53935", "GameEvent", false, "G", "goal");
        await repository.UpsertTagPresetAsync(preset, CancellationToken.None);

        var captureService = new EventCaptureService(repository, new VideoAnalysis.Core.Services.TagService());

        var opened = await captureService.RegisterHotkeyPressAsync(projectId, "G", 100, TeamSide.Home, CancellationToken.None);
        Assert.True(opened.IsOpen);
        Assert.Equal(100, opened.StartFrame);
        Assert.Equal(100, opened.EndFrame);

        var closed = await captureService.RegisterHotkeyPressAsync(projectId, "G", 135, TeamSide.Home, CancellationToken.None);
        Assert.False(closed.IsOpen);
        Assert.Equal(opened.Id, closed.Id);
        Assert.Equal(135, closed.EndFrame);

        var openEvents = await repository.GetTagEventsAsync(projectId, new TagQuery(preset.Id, null, null, null, null, true), CancellationToken.None);
        var closedEvents = await repository.GetTagEventsAsync(projectId, new TagQuery(preset.Id, null, null, null, TeamSide.Home, false), CancellationToken.None);
        Assert.Empty(openEvents);
        Assert.Single(closedEvents);
    }

    [Fact]
    public async Task UpsertAndLoadPlaylist_Works()
    {
        var repository = new SqliteProjectRepository(_storageRootPath);
        await repository.InitializeAsync(CancellationToken.None);

        var projectId = Guid.NewGuid();
        await repository.CreateProjectAsync(
            new Project(projectId, "Playlist", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, Path.Combine(_storageRootPath, "playlist")),
            CancellationToken.None);

        var presetId = Guid.NewGuid();
        await repository.UpsertTagPresetAsync(
            new TagPreset(presetId, projectId, "Goal", "#E53935", "GameEvent", false, "G", "goal"),
            CancellationToken.None);

        var tagEventId = Guid.NewGuid();
        await repository.UpsertTagEventAsync(
            new TagEvent(tagEventId, projectId, presetId, 100, 140, "Player A", "1", null, DateTimeOffset.UtcNow, TeamSide.Home, false),
            CancellationToken.None);

        var playlist = new Playlist(
            Guid.NewGuid(),
            projectId,
            "Goals playlist",
            "Home goals",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var item = new PlaylistItem(
            Guid.NewGuid(),
            playlist.Id,
            tagEventId,
            presetId,
            0,
            100,
            140,
            90,
            150,
            10,
            10,
            "Goal",
            "Player A",
            TeamSide.Home);

        await repository.UpsertPlaylistAsync(playlist, CancellationToken.None);
        await repository.ReplacePlaylistItemsAsync(playlist.Id, [item], CancellationToken.None);

        var playlists = await repository.GetPlaylistsAsync(projectId, CancellationToken.None);
        var loadedPlaylist = await repository.GetPlaylistAsync(projectId, playlist.Id, CancellationToken.None);
        var loadedItems = await repository.GetPlaylistItemsAsync(playlist.Id, CancellationToken.None);

        Assert.Single(playlists);
        Assert.NotNull(loadedPlaylist);
        Assert.Equal("Goals playlist", loadedPlaylist!.Name);
        Assert.Single(loadedItems);
        Assert.Equal(90, loadedItems[0].ClipStartFrame);
        Assert.Equal(150, loadedItems[0].ClipEndFrame);
        Assert.Equal("Goal", loadedItems[0].Label);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageRootPath))
        {
            Directory.Delete(_storageRootPath, recursive: true);
        }
    }
}
