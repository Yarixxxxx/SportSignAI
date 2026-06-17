using VideoAnalysis.App.Configuration;
using VideoAnalysis.App.Media;
using VideoAnalysis.App.ViewModels.Shell;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Dtos;
using VideoAnalysis.Core.Enums;
using VideoAnalysis.Core.Models;
using VideoAnalysis.Core.Services;
using VideoAnalysis.Infrastructure.Persistence;
using VideoAnalysis.Infrastructure.Services;

namespace VideoAnalysis.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _tempRootPath;
    private readonly string _settingsPath;
    private readonly string _projectsRootPath;
    private readonly string _sourceVideoPath;

    public MainWindowViewModelTests()
    {
        _tempRootPath = Path.Combine(Path.GetTempPath(), "video-analysis-vm-tests", Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_tempRootPath, "settings.json");
        _projectsRootPath = Path.Combine(_tempRootPath, "projects");
        Directory.CreateDirectory(_tempRootPath);
        Directory.CreateDirectory(_projectsRootPath);

        _sourceVideoPath = CreateSourceVideoFile("match.mp4");
    }

    [Fact]
    public async Task ContinueNewProjectCommand_CreatesProjectAndLoadsImportedVideo()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var mediaPlaybackService = new FakeMediaPlaybackService();
        var viewModel = new MainWindowViewModel(
            repository,
            projectSetupService,
            new PlaylistService(repository),
            new TagService(),
            new FakeClipComposerService(),
            new FakeExportService(),
            mediaPlaybackService,
            new FakeVideoProxyService(),
            new AppSettingsStore(_settingsPath),
            new AppSettings());

        viewModel.NewProjectName = "Integration Match";
        viewModel.NewProjectHomeTeam = "Home";
        viewModel.NewProjectAwayTeam = "Away";
        viewModel.NewProjectVideoPath = _sourceVideoPath;
        viewModel.IsNewProjectDialogOpen = true;

        await viewModel.ContinueNewProjectCommand.ExecuteAsync(null);

        var projects = await repository.ListProjectsAsync(CancellationToken.None);
        var project = Assert.Single(projects);
        var projectVideo = await repository.GetProjectVideoAsync(project.Id, CancellationToken.None);

        Assert.False(viewModel.IsNewProjectDialogOpen);
        Assert.Equal(project.Name, viewModel.ProjectName);
        Assert.NotNull(projectVideo);
        Assert.Equal(projectVideo!.StoredFilePath, viewModel.SourceVideoPath);
        Assert.True(File.Exists(_sourceVideoPath));
        Assert.True(File.Exists(projectVideo.StoredFilePath));
        Assert.Contains($"{Path.DirectorySeparatorChar}media{Path.DirectorySeparatorChar}", projectVideo.StoredFilePath);
        Assert.Equal(25, viewModel.FramesPerSecond);
        Assert.Equal(250, viewModel.DurationFrames);
        Assert.NotEmpty(viewModel.TagPresets);
        Assert.False(viewModel.IsStartupScreenOpen);
        Assert.Contains("created", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContinueNewProjectCommand_WithBroadcastMode_CreatesProjectWithoutVideo()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());

        viewModel.NewProjectName = "Live Integration Match";
        viewModel.NewProjectBroadcastMode = true;
        viewModel.IsNewProjectDialogOpen = true;

        await viewModel.ContinueNewProjectCommand.ExecuteAsync(null);

        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var projectVideo = await repository.GetProjectVideoAsync(project.Id, CancellationToken.None);

        Assert.True(project.IsBroadcastMode);
        Assert.Null(projectVideo);
        Assert.True(viewModel.IsBroadcastModeProject);
        Assert.Equal(string.Empty, viewModel.SourceVideoPath);
        Assert.False(viewModel.IsNewProjectDialogOpen);
        Assert.False(viewModel.IsStartupScreenOpen);
    }

    [Fact]
    public async Task ContinueNewProjectCommand_ResetsPreviousPlayerAndBroadcastState()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var mediaPlaybackService = new FakeMediaPlaybackService();
        var viewModel = CreateViewModel(repository, projectSetupService, mediaPlaybackService);

        viewModel.NewProjectName = "Recorded Match";
        viewModel.NewProjectVideoPath = _sourceVideoPath;
        await viewModel.ContinueNewProjectCommand.ExecuteAsync(null);
        viewModel.TogglePlayPauseCommand.Execute(null);
        viewModel.IsBroadcastDvrRunning = true;
        viewModel.IsBroadcastRecording = true;
        viewModel.BroadcastDvrPreviewSource = "udp://old-preview";
        viewModel.BroadcastRecordingPreviewSource = "old-recording.ts";
        viewModel.StartBroadcastTimeline(DateTimeOffset.UtcNow.AddSeconds(-30));

        viewModel.NewProjectName = "Fresh Live Match";
        viewModel.NewProjectVideoPath = string.Empty;
        viewModel.NewProjectBroadcastMode = true;
        viewModel.IsNewProjectDialogOpen = true;

        await viewModel.ContinueNewProjectCommand.ExecuteAsync(null);

        Assert.Equal(2, (await repository.ListProjectsAsync(CancellationToken.None)).Count);
        Assert.True(viewModel.IsBroadcastModeProject);
        Assert.Equal(string.Empty, viewModel.SourceVideoPath);
        Assert.Equal(string.Empty, viewModel.PlaybackVideoPath);
        Assert.False(viewModel.IsPlaying);
        Assert.False(viewModel.IsBroadcastDvrRunning);
        Assert.False(viewModel.IsBroadcastRecording);
        Assert.Equal(string.Empty, viewModel.BroadcastDvrPreviewSource);
        Assert.Equal(string.Empty, viewModel.BroadcastRecordingPreviewSource);
        Assert.Equal(0, viewModel.CurrentFrame);
        Assert.Equal(0, viewModel.BroadcastTimelineFrame);
        Assert.Equal(1, viewModel.DurationFrames);
        Assert.False(viewModel.IsBroadcastTimelineActive);
        Assert.True(mediaPlaybackService.CloseCallCount > 0);
    }

    [Fact]
    public async Task BroadcastProject_WithStoppedDvrArchive_CanExportWithoutSourceVideo()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var result = await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Stopped Broadcast", string.Empty, "Stopped Broadcast", IsBroadcastMode: true),
            CancellationToken.None);

        CreateFinalizedDvrSegment(result.ProjectFolderPath, "session-20260616-120000", "segment-000000.ts");

        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Stopped Broadcast");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBroadcastModeProject);
        Assert.Equal(string.Empty, viewModel.SourceVideoPath);

        viewModel.OpenExportDialogCommand.Execute(null);

        Assert.True(viewModel.IsExportDialogOpen);
        Assert.True(viewModel.CanExportFromDialog);
    }

    [Fact]
    public async Task BroadcastDvrService_AttachesLatestStoppedArchive()
    {
        var projectFolderPath = Path.Combine(_tempRootPath, "broadcast-archive-project");
        CreateFinalizedDvrSegment(projectFolderPath, "session-20260616-120000", "segment-000000.ts");

        var service = new BroadcastDvrService("ffmpeg");

        Assert.True(service.TryAttachLatestSession(projectFolderPath));
        Assert.True(service.HasExportableArchive);
        Assert.True(service.GetAvailableFrameLimit() >= 29);

        var index = await service.SaveIndexAsync(CancellationToken.None);
        var segment = Assert.Single(index.Segments);
        Assert.Equal(0, segment.StartFrame);
        Assert.True(segment.EndFrame >= 29);
    }

    [Fact]
    public async Task InitializeCommand_WithExistingProjects_ShowsStartupScreenAndRecentProjects()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Existing Match", CreateSourceVideoFile("existing.mp4"), "Existing Match"),
            CancellationToken.None);

        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());

        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsStartupScreenOpen);
        Assert.True(viewModel.HasRecentProjects);
        Assert.NotNull(viewModel.SelectedRecentProject);
        Assert.Equal(string.Empty, viewModel.SourceVideoPath);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public async Task OpenSelectedRecentProjectCommand_LoadsProjectFromStartupScreen()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);

        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("First Match", CreateSourceVideoFile("first.mp4"), "First Match"),
            CancellationToken.None);
        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Second Match", CreateSourceVideoFile("second.mp4"), "Second Match"),
            CancellationToken.None);

        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());
        await viewModel.InitializeCommand.ExecuteAsync(null);

        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, (project) => project.Name == "Second Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStartupScreenOpen);
        Assert.Equal("Second Match", viewModel.ProjectName);
        Assert.NotEmpty(viewModel.TagPresets);
        Assert.NotEqual(string.Empty, viewModel.SourceVideoPath);
        Assert.Contains("opened", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlaylistCommand_CreatesPlaylistAndLoadsPlaylistItems()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);

        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Playlist Match", CreateSourceVideoFile("playlist.mp4"), "Playlist Match"),
            CancellationToken.None);

        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Playlist Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        var preset = viewModel.TagPresets.First((candidate) => candidate.IconKey == "goal");
        await repository.UpsertTagEventAsync(
            new TagEvent(Guid.NewGuid(), viewModel.RecentProjects[0].ProjectId, preset.Id, 100, 130, "Player A", "1", null, DateTimeOffset.UtcNow, TeamSide.Home, false),
            CancellationToken.None);
        await repository.UpsertTagEventAsync(
            new TagEvent(Guid.NewGuid(), viewModel.RecentProjects[0].ProjectId, preset.Id, 220, 250, "Player B", "2", null, DateTimeOffset.UtcNow, TeamSide.Away, false),
            CancellationToken.None);

        await viewModel.OpenStartupScreenCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Playlist Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        viewModel.TogglePlaylistSelectionCommand.Execute(viewModel.TagEvents[0]);
        viewModel.TogglePlaylistSelectionCommand.Execute(viewModel.TagEvents[1]);
        viewModel.PlaylistName = "Goals playlist";
        viewModel.PreRollFrames = 10;
        viewModel.PostRollFrames = 20;

        await viewModel.CreatePlaylistCommand.ExecuteAsync(null);

        var playlists = await repository.GetPlaylistsAsync(viewModel.RecentProjects[0].ProjectId, CancellationToken.None);
        var playlist = Assert.Single(playlists);
        var playlistItems = await repository.GetPlaylistItemsAsync(playlist.Id, CancellationToken.None);

        Assert.Single(viewModel.Playlists);
        Assert.Equal(2, viewModel.PlaylistItems.Count);
        Assert.Equal("Goals playlist", playlist.Name);
        Assert.Equal(90, playlistItems[0].ClipStartFrame);
        Assert.Equal(250, playlistItems[1].ClipEndFrame);
        Assert.Contains("Goals playlist", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleEventTypeHotkeyAsync_Twice_SavesClosedEvent()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);

        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Hotkey Match", CreateSourceVideoFile("hotkey.mp4"), "Hotkey Match"),
            CancellationToken.None);

        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Hotkey Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        var preset = viewModel.TagPresets.First(candidate => string.Equals(candidate.Hotkey, "G", StringComparison.OrdinalIgnoreCase));

        viewModel.CurrentFrame = 100;
        await viewModel.HandleEventTypeHotkeyAsync("G");

        Assert.True(viewModel.IsTagEventEditorOpen);
        Assert.Equal(preset.Id, viewModel.SelectedPreset?.Id);
        Assert.Equal(100, viewModel.TagEndFrame);

        viewModel.CurrentFrame = 135;
        await viewModel.HandleEventTypeHotkeyAsync("G");

        Assert.False(viewModel.IsTagEventEditorOpen);

        var savedEvents = await repository.GetTagEventsAsync(
            viewModel.SelectedRecentProject!.ProjectId,
            new TagQuery(preset.Id, null, null, null, null, false),
            CancellationToken.None);

        var savedEvent = Assert.Single(savedEvents);
        Assert.Equal(100, savedEvent.StartFrame);
        Assert.Equal(135 + preset.PostRollFrames, savedEvent.EndFrame);
        Assert.Equal(TeamSide.Home, savedEvent.TeamSide);
    }

    [Fact]
    public async Task HandleEventTypeHotkeyAsync_DuringBroadcastClipPlayback_UsesLiveTimelineFrame()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());

        viewModel.NewProjectName = "Live Clip Tagging";
        viewModel.NewProjectBroadcastMode = true;
        await viewModel.ContinueNewProjectCommand.ExecuteAsync(null);

        var recordedClipPath = Path.Combine(_tempRootPath, "recorded-clip.mp4");
        File.WriteAllText(recordedClipPath, "recorded clip");
        SetPrivateField(viewModel, "_currentBroadcastRecordingPath", recordedClipPath);
        SetPrivateField(viewModel, "_currentBroadcastRecordingStartFrame", 1_000L);
        viewModel.SourceVideoPath = recordedClipPath;
        viewModel.PlaybackVideoPath = recordedClipPath;
        viewModel.IsLiveSource = false;
        viewModel.CurrentFrame = 42;
        viewModel.StartBroadcastTimeline(DateTimeOffset.UtcNow.AddSeconds(-120));

        var preset = viewModel.TagPresets.First(candidate => string.Equals(candidate.Hotkey, "G", StringComparison.OrdinalIgnoreCase));

        await viewModel.HandleEventTypeHotkeyAsync("G");

        Assert.True(viewModel.IsTagEventEditorOpen);
        Assert.Equal(preset.Id, viewModel.SelectedPreset?.Id);
        Assert.True(viewModel.TagEndFrame > 2_000);
        Assert.NotEqual(1_042, viewModel.TagEndFrame);
    }

    [Fact]
    public async Task ExportCommand_EnrichesSegmentsWithEventOverlayMetadata()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);

        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Overlay Match", CreateSourceVideoFile("overlay.mp4"), "Overlay Match", "Playoffs", "Молот", "Химик"),
            CancellationToken.None);

        var fakeExportService = new FakeExportService();
        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService(), fakeExportService);
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Overlay Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        var preset = viewModel.TagPresets.First(candidate => candidate.IconKey == "goal");
        await repository.UpsertTagEventAsync(
            new TagEvent(Guid.NewGuid(), viewModel.SelectedRecentProject!.ProjectId, preset.Id, 100, 130, "Иванов", "P2", null, DateTimeOffset.UtcNow, TeamSide.Home, false),
            CancellationToken.None);

        viewModel.SelectedPreset = preset;
        viewModel.ExportOutputPath = Path.Combine(_tempRootPath, "exports", "overlay.mp4");

        await viewModel.BuildClipsCommand.ExecuteAsync(null);
        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.NotNull(fakeExportService.LastRequest);
        var request = fakeExportService.LastRequest!;
        var segment = Assert.Single(request.Segments);
        Assert.Equal("Гол", segment.Label);
        Assert.Equal("Молот", segment.TeamName);
        Assert.Equal("Иванов", segment.Player);
        Assert.Equal("P2", segment.Period);
        Assert.Equal("00:00:04", segment.MatchClockText);
        Assert.Equal("#E53935", segment.AccentColorHex);
        Assert.Equal("1/1", segment.CounterText);
    }

    [Fact]
    public async Task ExportCommand_UsesBroadcastTimelineLengthWhenPlayerHasShortClip()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);

        await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Broadcast Export", CreateSourceVideoFile("broadcast-source.mp4"), "Broadcast Export"),
            CancellationToken.None);

        var fakeExportService = new FakeExportService();
        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService(), fakeExportService);
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Broadcast Export");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        viewModel.IsBroadcastModeProject = true;
        viewModel.StartBroadcastTimeline(DateTimeOffset.UtcNow.AddSeconds(-30));

        var preset = viewModel.TagPresets.First(candidate => candidate.IconKey == "goal");
        await repository.UpsertTagEventAsync(
            new TagEvent(Guid.NewGuid(), viewModel.SelectedRecentProject!.ProjectId, preset.Id, 500, 530, null, null, null, DateTimeOffset.UtcNow, TeamSide.Home, false),
            CancellationToken.None);

        viewModel.SelectedPreset = preset;
        viewModel.ExportOutputPath = Path.Combine(_tempRootPath, "exports", "broadcast-export.mp4");

        await viewModel.ExportCommand.ExecuteAsync(null);
        viewModel.StopBroadcastTimeline();

        Assert.NotNull(fakeExportService.LastRequest);
        var segment = Assert.Single(fakeExportService.LastRequest!.Segments);
        Assert.Equal(470, segment.StartFrame);
        Assert.Equal(560, segment.EndFrame);
    }

    [Fact]
    public async Task OpenSelectedPlaylistCommand_RepairsSingleFramePlaylistItemsFromEventRange()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);

        var result = await projectSetupService.CreateProjectWithVideoAsync(
            new CreateProjectRequestDto("Repair Match", CreateSourceVideoFile("repair.mp4"), "Repair Match"),
            CancellationToken.None);

        var viewModel = CreateViewModel(repository, projectSetupService, new FakeMediaPlaybackService());
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Repair Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);

        var projectId = result.ProjectId;
        var preset = viewModel.TagPresets.First(candidate => candidate.IconKey == "goal");
        var tagEvent = new TagEvent(
            Guid.NewGuid(),
            projectId,
            preset.Id,
            100,
            130,
            "Player A",
            "1",
            null,
            DateTimeOffset.UtcNow,
            TeamSide.Home,
            false);

        await repository.UpsertTagEventAsync(tagEvent, CancellationToken.None);

        var playlist = new Playlist(
            Guid.NewGuid(),
            projectId,
            "Legacy Playlist",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var legacyItem = new PlaylistItem(
            Guid.NewGuid(),
            playlist.Id,
            tagEvent.Id,
            preset.Id,
            0,
            tagEvent.StartFrame,
            tagEvent.EndFrame,
            tagEvent.StartFrame,
            tagEvent.StartFrame,
            10,
            20,
            preset.Name,
            tagEvent.Player,
            tagEvent.TeamSide);

        await repository.UpsertPlaylistAsync(playlist, CancellationToken.None);
        await repository.ReplacePlaylistItemsAsync(playlist.Id, [legacyItem], CancellationToken.None);

        await viewModel.OpenStartupScreenCommand.ExecuteAsync(null);
        viewModel.SelectedRecentProject = Assert.Single(viewModel.RecentProjects, project => project.Name == "Repair Match");
        await viewModel.OpenSelectedRecentProjectCommand.ExecuteAsync(null);
        viewModel.SelectedPlaylist = Assert.Single(viewModel.Playlists, candidate => candidate.Name == "Legacy Playlist");

        await viewModel.OpenSelectedPlaylistCommand.ExecuteAsync(null);

        var loadedItem = Assert.Single(viewModel.PlaylistItems);
        Assert.Equal(90, loadedItem.ClipStartFrame);
        Assert.Equal(150, loadedItem.ClipEndFrame);
        Assert.Equal("00:00:03 → 00:00:06", loadedItem.FrameRangeText);
        Assert.Contains("Восстановлены диапазоны", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SetPlaybackRateCommand_WhenRateChangeInterruptsPlayback_ResumesPlayback()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var mediaPlaybackService = new FakeMediaPlaybackService
        {
            InterruptPlaybackOnRateChange = true
        };
        var viewModel = CreateViewModel(repository, projectSetupService, mediaPlaybackService);

        mediaPlaybackService.Play();
        viewModel.SetPlaybackRateCommand.Execute("1.5x");

        Assert.Equal(1.5d, mediaPlaybackService.PlaybackRate);
        Assert.True(mediaPlaybackService.IsPlaying);
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(2, mediaPlaybackService.PlayCallCount);
    }

    [Fact]
    public void CurrentFrameChanged_WhenSeekInterruptsPlayback_ResumesPlayback()
    {
        var repository = new SqliteProjectRepository(_projectsRootPath);
        var projectSetupService = new ProjectSetupService(repository, _projectsRootPath);
        var mediaPlaybackService = new FakeMediaPlaybackService
        {
            InterruptPlaybackOnSeek = true
        };
        var viewModel = CreateViewModel(repository, projectSetupService, mediaPlaybackService);

        mediaPlaybackService.Play();
        viewModel.CurrentFrame = 50;

        Assert.Equal(50, mediaPlaybackService.CurrentFrame);
        Assert.True(mediaPlaybackService.IsPlaying);
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(2, mediaPlaybackService.PlayCallCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRootPath))
        {
            Directory.Delete(_tempRootPath, recursive: true);
        }
    }

    private MainWindowViewModel CreateViewModel(
        SqliteProjectRepository repository,
        ProjectSetupService projectSetupService,
        FakeMediaPlaybackService mediaPlaybackService,
        FakeExportService? exportService = null)
    {
        return new MainWindowViewModel(
            repository,
            projectSetupService,
            new PlaylistService(repository),
            new TagService(),
            new FakeClipComposerService(),
            exportService ?? new FakeExportService(),
            mediaPlaybackService,
            new FakeVideoProxyService(),
            new AppSettingsStore(_settingsPath),
            new AppSettings());
    }

    private string CreateSourceVideoFile(string fileName)
    {
        var sourceFolder = Path.Combine(_tempRootPath, "source");
        Directory.CreateDirectory(sourceFolder);
        var videoPath = Path.Combine(sourceFolder, fileName);
        File.WriteAllText(videoPath, "video");
        return videoPath;
    }

    private static string CreateFinalizedDvrSegment(string projectFolderPath, string sessionName, string fileName)
    {
        var segmentFolderPath = Path.Combine(projectFolderPath, "media", "live-dvr", sessionName, "segments");
        Directory.CreateDirectory(segmentFolderPath);
        var segmentPath = Path.Combine(segmentFolderPath, fileName);
        File.WriteAllText(segmentPath, "transport stream");
        File.SetLastWriteTimeUtc(segmentPath, DateTime.UtcNow.AddSeconds(-2));
        return segmentPath;
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class FakeMediaPlaybackService : IMediaPlaybackService
    {
        public event EventHandler? PlaybackStateChanged;
        public event EventHandler<long>? FrameChanged;

        public bool IsPlaying { get; private set; }
        public bool IsMuted { get; private set; }
        public long CurrentFrame { get; private set; }
        public long DurationFrames { get; private set; } = 250;
        public double FramesPerSecond { get; private set; } = 25;
        public long VideoWidth { get; private set; } = 1920;
        public long VideoHeight { get; private set; } = 1080;
        public int Volume { get; private set; } = 100;
        public double PlaybackRate { get; private set; } = 1.0;
        public double VideoZoom { get; private set; } = 1.0;
        public double VideoZoomCenterX { get; private set; } = 0.5;
        public double VideoZoomCenterY { get; private set; } = 0.5;
        public bool InterruptPlaybackOnSeek { get; init; }
        public bool InterruptPlaybackOnRateChange { get; init; }
        public int PlayCallCount { get; private set; }
        public int CloseCallCount { get; private set; }

        public Task<MediaMetadata> OpenAsync(string filePath, CancellationToken cancellationToken)
        {
            DurationFrames = 250;
            FramesPerSecond = 25;
            VideoWidth = 1920;
            VideoHeight = 1080;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new MediaMetadata(filePath, FramesPerSecond, DurationFrames, VideoWidth, VideoHeight));
        }

        public Task<MediaMetadata> OpenLiveCameraAsync(string? deviceName, CancellationToken cancellationToken)
        {
            DurationFrames = 250;
            FramesPerSecond = 25;
            VideoWidth = 1920;
            VideoHeight = 1080;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new MediaMetadata($"camera://{deviceName ?? "default"}", FramesPerSecond, DurationFrames, VideoWidth, VideoHeight));
        }

        public void Play()
        {
            PlayCallCount++;
            IsPlaying = true;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Close()
        {
            CloseCallCount++;
            IsPlaying = false;
            CurrentFrame = 0;
            DurationFrames = 1;
            FramesPerSecond = 30;
            VideoWidth = 0;
            VideoHeight = 0;
            PlaybackRate = 1.0;
            VideoZoom = 1.0;
            VideoZoomCenterX = 0.5;
            VideoZoomCenterY = 0.5;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            FrameChanged?.Invoke(this, CurrentFrame);
        }

        public void Pause()
        {
            IsPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SeekToFrame(long frame)
        {
            CurrentFrame = frame;
            if (InterruptPlaybackOnSeek && IsPlaying)
            {
                IsPlaying = false;
            }

            FrameChanged?.Invoke(this, frame);
        }

        public void StepFrameForward()
        {
            SeekToFrame(CurrentFrame + 1);
        }

        public void StepFrameBackward()
        {
            SeekToFrame(Math.Max(0, CurrentFrame - 1));
        }

        public void SetVolume(int volume)
        {
            Volume = volume;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetPlaybackRate(double playbackRate)
        {
            PlaybackRate = playbackRate;
            if (InterruptPlaybackOnRateChange && IsPlaying)
            {
                IsPlaying = false;
            }

            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetVideoZoom(double zoom, double centerX, double centerY, double viewportWidth, double viewportHeight)
        {
            VideoZoom = zoom;
            VideoZoomCenterX = centerX;
            VideoZoomCenterY = centerY;
        }

        public void ToggleMute()
        {
            IsMuted = !IsMuted;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeClipComposerService : IClipComposerService
    {
        public IReadOnlyList<ClipSegmentDto> BuildSegments(IEnumerable<TagEvent> events, ClipRecipe recipe, long maxFrame)
        {
            return new FfmpegClipComposerService("ffmpeg").BuildSegments(events, recipe, maxFrame);
        }

        public Task<string> ComposeAsync(
            string sourceVideoPath,
            IReadOnlyList<ClipSegmentDto> segments,
            string outputPath,
            double framesPerSecond,
            string? overlayFilterPath,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(outputPath);
        }
    }

    private sealed class FakeVideoProxyService : IVideoProxyService
    {
        public Task<VideoProxyResult> EnsureProxyAsync(
            string sourceVideoPath,
            string projectFolderPath,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new VideoProxyResult(sourceVideoPath, Created: false));
        }
    }

    private sealed class FakeExportService : IExportService
    {
        public ExportRequestDto? LastRequest { get; private set; }

        public Task<ExportResultDto> ExportAsync(ExportRequestDto request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ExportResultDto(true, request.OutputPath, null, null, null));
        }
    }
}


