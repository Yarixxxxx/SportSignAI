using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoAnalysis.App.Configuration;
using VideoAnalysis.App.Media;
using VideoAnalysis.App.Services;
using VideoAnalysis.App.ViewModels.Base;
using VideoAnalysis.App.ViewModels.Items;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Dtos;
using VideoAnalysis.Core.Enums;
using VideoAnalysis.Core.Models;
using VideoAnalysis.Infrastructure.Media;
#if WINDOWS_MPV
using MpvContext = HanumanInstitute.LibMpv.MpvContext;
#endif

namespace VideoAnalysis.App.ViewModels.Shell;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IProjectRepository _repository;
    private readonly IProjectSetupService _projectSetupService;
    private readonly IPlaylistService _playlistService;
    private readonly ITagService _tagService;
    private readonly IClipComposerService _clipComposerService;
    private readonly IExportService _exportService;
    private readonly IMediaPlaybackService _mediaPlaybackService;
    private readonly IVideoProxyService _videoProxyService;
    private readonly DispatcherTimer _broadcastTimelineTimer;
    private readonly BroadcastDvrService _broadcastDvrService;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private Guid _projectId;
    private bool _ignoreFrameChange;
    private bool _isAdjustingEventTypeHotkey;
    private string _lastValidEventTypeHotkey = string.Empty;
    private readonly HashSet<Guid> _selectedPlaylistTagEventIds = [];
    private IReadOnlyList<ClipSegmentDto> _lastSegments = [];
    private IReadOnlyList<ClipSegmentDto> _activePlaylistSegments = [];
    private int _activePlaylistSegmentIndex = -1;
    private Guid _activePlaylistId;
    private string _projectFolderPath = string.Empty;
    private IReadOnlyList<TagEvent> _allTimelineEvents = [];
    private double _timelineViewportWidth;
    private double _timelineViewportOffsetX;
    private double _timelineZoom = 1d;
    private double _videoZoomCenterX = 0.5d;
    private double _videoZoomCenterY = 0.5d;
    private double _videoViewportWidth;
    private double _videoViewportHeight;
    private DateTimeOffset _broadcastTimelineStartedAtUtc;
    private string? _currentBroadcastRecordingPath;
    private long _broadcastRecordingStartFrame;
    private long _currentBroadcastRecordingStartFrame;
    private long _broadcastTimelineOffsetFrame;
    private long _lastBroadcastTimelineLayoutRefreshFrame = -1;

    private const double TimelineSecondWidth = 8d;
    private const double TimelineMinimumWidth = 216d;
    private const double TimelineInstantWidth = 4d;
    private const double TimelineFallbackMinimumZoom = 0.001d;
    private const double TimelineMinimumZoom = 1d;
    private const double TimelineMaximumZoom = 15d;
    private const double TimelineTickMinimumSpacing = 72d;
    private const double VideoZoomMinimum = 1d;
    private const double VideoZoomMaximum = 4d;
    private const double VideoZoomStep = 0.5d;
    private const double VideoZoomOutStep = 1.0d;

    public MainWindowViewModel(
        IProjectRepository repository,
        IProjectSetupService projectSetupService,
        IPlaylistService playlistService,
        ITagService tagService,
        IClipComposerService clipComposerService,
        IExportService exportService,
        IMediaPlaybackService mediaPlaybackService,
        IVideoProxyService videoProxyService,
        AppSettingsStore settingsStore,
        AppSettings settings)
    {
        _repository = repository;
        _projectSetupService = projectSetupService;
        _playlistService = playlistService;
        _tagService = tagService;
        _clipComposerService = clipComposerService;
        _exportService = exportService;
        _mediaPlaybackService = mediaPlaybackService;
        _videoProxyService = videoProxyService;
        _settingsStore = settingsStore;
        _settings = settings;
        _broadcastDvrService = new BroadcastDvrService(_settings.FfmpegPath);
        _broadcastTimelineTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _broadcastTimelineTimer.Tick += (_, _) => UpdateBroadcastTimeline();

        RecentProjects.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecentProjects));
            OnPropertyChanged(nameof(HasNoRecentProjects));
            OnPropertyChanged(nameof(CanOpenSelectedRecentProject));
        };
        ProjectPickerItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProjectPickerItems));
            OnPropertyChanged(nameof(HasNoProjectPickerItems));
        };
        Playlists.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPlaylists));
            OnPropertyChanged(nameof(CanOpenAddToPlaylistDialog));
            OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
        };
        PlaylistItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPlaylistItems));
            OnPropertyChanged(nameof(HasNoPlaylistItems));
        };

        ExportOutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "video-analysis-export.mp4");
        ExportFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Video Analytics", "Exports");
        PlaylistName = "Новая подборка";
        YandexServiceUrl = _settings.YandexServiceUrl;
        YandexBucket = _settings.YandexBucket;
        YandexAccessKey = _settings.YandexAccessKey;
        YandexSecretKey = _settings.YandexSecretKey;
        YandexRegion = _settings.YandexRegion;
        YandexPrefix = _settings.YandexPrefix;

        _mediaPlaybackService.FrameChanged += OnPlaybackFrameChanged;
        _mediaPlaybackService.PlaybackStateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            DurationFrames = Math.Max(1, _mediaPlaybackService.DurationFrames);
            FramesPerSecond = _mediaPlaybackService.FramesPerSecond;
            VideoWidth = _mediaPlaybackService.VideoWidth;
            VideoHeight = _mediaPlaybackService.VideoHeight;
            IsPlaying = _mediaPlaybackService.IsPlaying;
            IsMuted = _mediaPlaybackService.IsMuted;
            PlaybackRate = _mediaPlaybackService.PlaybackRate <= 0 ? 1.0 : _mediaPlaybackService.PlaybackRate;
            OnPropertyChanged(nameof(CurrentTimeText));
            OnPropertyChanged(nameof(DurationTimeText));
        }, DispatcherPriority.Background);
        RefreshPlaybackUiState();
    }

    public ObservableCollection<TagPreset> TagPresets { get; } = [];
    public ObservableCollection<EventTypeItemViewModel> EventTypeItems { get; } = [];
    public ObservableCollection<TagEventItemViewModel> TagEvents { get; } = [];
    public ObservableCollection<AnnotationItemViewModel> Annotations { get; } = [];
    public ObservableCollection<RecentProjectItemViewModel> RecentProjects { get; } = [];
    public ObservableCollection<RecentProjectItemViewModel> ProjectPickerItems { get; } = [];
    public ObservableCollection<PlaylistSummaryItemViewModel> Playlists { get; } = [];
    public ObservableCollection<PlaylistClipItemViewModel> PlaylistItems { get; } = [];
    public ObservableCollection<StatisticsBarItemViewModel> StatisticsItems { get; } = [];
    public ObservableCollection<TimelineRowItemViewModel> TimelineRows { get; } = [];
    public ObservableCollection<TimelineTickItemViewModel> TimelineTicks { get; } = [];
    public ObservableCollection<TimelineFilterItemViewModel> TimelineFilters { get; } = [];
    public IReadOnlyList<AnnotationShapeType> ShapeTypes { get; } = Enum.GetValues<AnnotationShapeType>();
    public IReadOnlyList<TeamSide> EventTeamSides { get; } = [TeamSide.Home, TeamSide.Away];

    public IReadOnlyList<string> PlaybackRateOptions { get; } = ["0.25x", "0.5x", "0.75x", "1.0x", "1.25x", "1.5x", "2.0x"];
    public bool HasRecentProjects => RecentProjects.Count > 0;
    public bool HasNoRecentProjects => RecentProjects.Count == 0;
    public bool HasProjectPickerItems => ProjectPickerItems.Count > 0;
    public bool HasNoProjectPickerItems => ProjectPickerItems.Count == 0;
    public bool HasPlaylistSelection => _selectedPlaylistTagEventIds.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;
    public bool HasPlaylistItems => PlaylistItems.Count > 0;
    public bool HasNoPlaylistItems => PlaylistItems.Count == 0;
    public bool CanDeleteSelectedPreset => SelectedPreset is not null;
    public bool CanDeleteEditedPreset => IsEditingPreset && SelectedPreset is not null;
    public bool CanDeleteEditedTagEvent => IsEditingTagEvent && SelectedTagEvent is not null;
    public bool CanOpenSelectedRecentProject => SelectedRecentProject is not null;
    public bool CanCloseStartupScreen => _projectId != Guid.Empty;
    public bool CanCreatePlaylist => _projectId != Guid.Empty;
    public bool CanOpenAddToPlaylistDialog => _projectId != Guid.Empty && HasPlaylistSelection && HasPlaylists;
    public bool CanAddSelectedEventsToPlaylist => _projectId != Guid.Empty && HasPlaylistSelection && SelectedTargetPlaylistForAdd is not null;
    public bool CanOpenSelectedPlaylist => SelectedPlaylist is not null;
    public bool CanDeleteSelectedPlaylist => SelectedPlaylist is not null;
    public bool CanPlayActivePlaylist => _activePlaylistSegments.Count > 0;
    public int HomeScore { get; private set; }
    public int AwayScore { get; private set; }
    public string HomeTeamDisplayName { get; private set; } = "Команда хозяев";
    public string AwayTeamDisplayName { get; private set; } = "Команда гостей";
    public int SelectedPlaylistEventCount => _selectedPlaylistTagEventIds.Count;
    public bool IsEventTypesTabSelected => string.Equals(SelectedEventsPanelTab, "EventTypes", StringComparison.Ordinal);
    public bool IsEventsTabSelected => string.Equals(SelectedEventsPanelTab, "Events", StringComparison.Ordinal);
    public bool IsPlaylistsTabSelected => string.Equals(SelectedRightPanelTab, "Playlists", StringComparison.Ordinal);
    public bool IsStatisticsTabSelected => string.Equals(SelectedRightPanelTab, "Statistics", StringComparison.Ordinal);
    public bool IsPlayerPanelVisible => !IsPlayerPanelHidden;
    public bool IsPlayerSurfaceVisible => !IsNewProjectDialogOpen && !IsStartupScreenVisible && !IsExportDialogOpen;
    public bool IsTimelineVisible => !IsTimelineHidden;
    public bool IsEventsPanelVisible => !IsEventsPanelHidden;
    public bool IsAnalysisPanelVisible => !IsAnalysisPanelHidden;
    public bool IsBroadcastPanelVisible => IsBroadcastModeProject && !IsBroadcastPanelHidden;
    public bool IsBroadcastEntityAvailable => IsBroadcastModeProject;
    public bool IsStartupScreenVisible => IsStartupScreenOpen && !IsNewProjectDialogOpen;
    public string PresetEditorTitle => IsEditingPreset ? "Редактирование типа события" : "Новый тип события";
    public string TagEventEditorTitle => IsEditingTagEvent ? "Редактирование события" : "Новое событие";
    public LibVLCSharp.Shared.MediaPlayer? MediaPlayer => (_mediaPlaybackService as LibVlcMediaPlaybackService)?.MediaPlayer;
    public string CurrentTimeText => FormatTime(CurrentFrame, FramesPerSecond);
    public string DurationTimeText => IsLiveSource ? "LIVE" : FormatTime(DurationFrames, FramesPerSecond);
    public long TimelineCurrentFrame => IsBroadcastTimelineActive ? BroadcastTimelineFrame : CurrentFrame;
    public long TimelineDurationFrames => IsBroadcastTimelineActive ? Math.Max(1, BroadcastTimelineFrame) : DurationFrames;
    public string TimelineCurrentTimeText => FormatTime(TimelineCurrentFrame, FramesPerSecond);
    public string TimelineDurationTimeText => IsBroadcastTimelineActive ? "LIVE" : FormatTime(TimelineDurationFrames, FramesPerSecond);
    public string TimelineHeaderTimeText => $"{TimelineCurrentTimeText} / {TimelineDurationTimeText}";
    public string TagStartTimeText => FormatTime(TagStartFrame, FramesPerSecond);
    public string TagEndTimeText => FormatTime(TagEndFrame, FramesPerSecond);
    public string TimelineFilterButtonText => $"Фильтры ({VisibleTimelineFilterCount}/{TimelineFilters.Count})";
    public int VisibleTimelineFilterCount => TimelineFilters.Count((item) => item.IsVisible);
    public double TimelineCanvasWidth => _timelineViewportWidth > 0d
        ? Math.Max(1d, _timelineViewportWidth * _timelineZoom)
        : TimelineMinimumWidth;
    public double TimelineCurrentLineLeft => CalculateTimelineX(TimelineCurrentFrame);
    public double TimelineZoom
    {
        get => _timelineZoom;
        set
        {
            var nextZoom = Math.Clamp(value, TimelineMinimumZoom, TimelineMaximumZoom);
            if (!SetProperty(ref _timelineZoom, nextZoom))
            {
                return;
            }

            OnPropertyChanged(nameof(TimelineZoomText));
            OnPropertyChanged(nameof(TimelineCanvasWidth));
            OnPropertyChanged(nameof(TimelineCurrentLineLeft));
            RefreshTimelineTicks();
            RefreshTimelineRows();
        }
    }
    public double TimelineZoomMinimumValue => TimelineMinimumZoom;
    public double TimelineZoomMaximumValue => TimelineMaximumZoom;
    public string TimelineZoomText => $"{_timelineZoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x";
    public string PlaybackButtonText => IsPlaying ? "Pause" : "Play";
    public string PlaybackGlyph => IsPlaying ? "||" : "▶";
    public bool ShowOverlayPlayButton => !IsPlaying;
    public string PlaybackRateText => $"{PlaybackRate:0.##}x";
    public double VideoZoomCenterX => _videoZoomCenterX;
    public double VideoZoomCenterY => _videoZoomCenterY;
    public string VideoZoomText => $"{VideoZoom:0.##}x";
    public bool CanZoomVideoIn => VideoZoom < VideoZoomMaximum - 0.001d;
    public bool CanZoomVideoOut => VideoZoom > VideoZoomMinimum + 0.001d;
    public string VolumeGlyph => IsMuted || Volume == 0 ? "🔇" : "🔊";
    public bool CanUseBroadcastRecording => IsBroadcastModeProject
        && IsBroadcastDvrRunning
        && !IsBroadcastRecordingOperationInProgress;
    public bool CanToggleBroadcastLive => IsBroadcastModeProject
        && !IsBroadcastRecording
        && !IsBroadcastRecordingOperationInProgress;
    public string BroadcastLiveButtonText => IsBroadcastDvrRunning ? "Остановить трансляцию" : "Возобновить трансляцию";
    public string BroadcastLiveGlyph => IsBroadcastDvrRunning ? "■" : "▶";
    public string BroadcastRecordingButtonText => IsBroadcastRecording ? "Остановить запись" : "Начать запись";
    public string BroadcastRecordingGlyph => IsBroadcastRecording ? "■" : "●";
    public string BroadcastRecordingStatusText => IsBroadcastRecording
        ? "Фрагмент отмечается"
        : IsBroadcastDvrRunning ? "DVR пишет трансляцию" : "Трансляция остановлена";
    public string NewProjectVideoLabelText => NewProjectBroadcastMode ? "Видео для старта (опционально)" : "Видео *";
    public string NewProjectVideoPlaceholderText => NewProjectBroadcastMode ? "Можно оставить пустым и открыть камеру после создания" : "Путь к видеофайлу";
    public string NewProjectPrimaryButtonText => NewProjectBroadcastMode ? "Создать проект трансляции" : "Далее: Импортировать видео";
    public bool IsExportAllClipsSelected => SelectedExportSource == ExportSourceOption.AllClips;
    public bool IsExportPlaylistSelected => SelectedExportSource == ExportSourceOption.Playlist;
    public bool IsExportFullMatchSelected => SelectedExportSource == ExportSourceOption.FullMatch;
    public bool IsExportFormatMp4Selected => SelectedExportFormat == ExportFormatOption.Mp4;
    public bool IsExportFormatAviSelected => SelectedExportFormat == ExportFormatOption.Avi;
    public bool IsExportFormatMovSelected => SelectedExportFormat == ExportFormatOption.Mov;
    public bool IsExportQualityLowSelected => SelectedExportQuality == ExportQualityOption.Low720p;
    public bool IsExportQualityMediumSelected => SelectedExportQuality == ExportQualityOption.Medium1080p;
    public bool IsExportQualityHighSelected => SelectedExportQuality == ExportQualityOption.High4K;
    public bool IsExportDestinationFolderSelected => SelectedExportDestination == ExportDestinationOption.Folder;
    public bool IsExportDestinationTelegramSelected => SelectedExportDestination == ExportDestinationOption.Telegram;
    public bool IsExportDestinationBothSelected => SelectedExportDestination == ExportDestinationOption.Both;
    public bool CanExportFromDialog => _projectId != Guid.Empty
        && (HasReadableSourceVideo() || CanUseBroadcastDvrAsSource())
        && !string.IsNullOrWhiteSpace(ExportFolderPath)
        && !IsExportInProgress;
    public bool CanCloseExportDialog => !IsExportInProgress;

    public void SetVideoViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0
            || double.IsNaN(width) || double.IsInfinity(width)
            || double.IsNaN(height) || double.IsInfinity(height))
        {
            return;
        }

        if (Math.Abs(_videoViewportWidth - width) < 0.5d
            && Math.Abs(_videoViewportHeight - height) < 0.5d)
        {
            return;
        }

        _videoViewportWidth = width;
        _videoViewportHeight = height;
        ApplyVideoZoom();
    }

    public void SetTimelineViewport(double width, double offsetX, bool refreshContent = true)
    {
        width = Math.Max(0d, width);
        offsetX = Math.Max(0d, offsetX);
        if (Math.Abs(_timelineViewportWidth - width) < 1d
            && Math.Abs(_timelineViewportOffsetX - offsetX) < 1d)
        {
            return;
        }

        _timelineViewportWidth = width;
        _timelineViewportOffsetX = Math.Clamp(offsetX, 0d, Math.Max(0d, TimelineCanvasWidth - width));
        OnPropertyChanged(nameof(TimelineCanvasWidth));
        OnPropertyChanged(nameof(TimelineCurrentLineLeft));
        if (!refreshContent)
        {
            return;
        }

        RefreshTimelineTicks();
        RefreshTimelineRows();
    }

    public void ZoomTimeline(double wheelDelta)
    {
        var multiplier = wheelDelta > 0 ? 1.2d : 1d / 1.2d;
        TimelineZoom = _timelineZoom * multiplier;
    }
    public string ExportPrimaryButtonText => IsExportInProgress ? "Рендерим..." : "Экспортировать";

    [ObservableProperty] private string _projectName = "Hockey Analysis";
    [ObservableProperty] private string _sourceVideoPath = string.Empty;
    [ObservableProperty] private string _playbackVideoPath = string.Empty;
    [ObservableProperty] private bool _isLiveSource;
    [ObservableProperty] private bool _isBroadcastModeProject;
    [ObservableProperty] private bool _isBroadcastTimelineActive;
    [ObservableProperty] private long _broadcastTimelineFrame;
    [ObservableProperty] private double _framesPerSecond = 30;
    [ObservableProperty] private long _durationFrames = 1;
    [ObservableProperty] private long _videoWidth;
    [ObservableProperty] private long _videoHeight;
    [ObservableProperty] private long _currentFrame;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private int _volume = 100;
    [ObservableProperty] private bool _isTimelineFilterPopupOpen;

    [ObservableProperty] private double _playbackRate = 1.0;
    [ObservableProperty] private double _videoZoom = 1.0;
    [ObservableProperty] private string _filterPlayer = string.Empty;
    [ObservableProperty] private string _filterPeriod = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _tagPlayer = string.Empty;
    [ObservableProperty] private string _tagPeriod = string.Empty;
    [ObservableProperty] private string _tagNotes = string.Empty;
    [ObservableProperty] private TeamSide _tagTeamSide = TeamSide.Home;
    [ObservableProperty] private long _tagStartFrame;
    [ObservableProperty] private long _tagEndFrame = 1;
    [ObservableProperty] private int _preRollFrames = 30;
    [ObservableProperty] private int _postRollFrames = 30;
    [ObservableProperty] private string _clipSummary = "Segments: 0";
    [ObservableProperty] private string _playlistName = string.Empty;
    [ObservableProperty] private string _playlistDescription = string.Empty;
    [ObservableProperty] private string _selectedEventsPanelTab = "EventTypes";
    [ObservableProperty] private string _selectedRightPanelTab = "Playlists";
    [ObservableProperty] private bool _isPlayerPanelHidden;
    [ObservableProperty] private bool _isTimelineHidden;
    [ObservableProperty] private bool _isEventsPanelHidden;
    [ObservableProperty] private bool _isAnalysisPanelHidden;
    [ObservableProperty] private bool _isBroadcastPanelHidden = true;
    [ObservableProperty] private bool _isBroadcastRecording;
    [ObservableProperty] private bool _isBroadcastRecordingOperationInProgress;
    [ObservableProperty] private string _broadcastRecordingPreviewSource = string.Empty;
    [ObservableProperty] private string _broadcastDvrPreviewSource = string.Empty;
    [ObservableProperty] private bool _isBroadcastDvrRunning;
    [ObservableProperty] private bool _isPresetEditorOpen;
    [ObservableProperty] private bool _isEditingPreset;
    [ObservableProperty] private bool _isTagEventEditorOpen;
    [ObservableProperty] private bool _isAddToPlaylistDialogOpen;
    [ObservableProperty] private bool _isEditingTagEvent;
    [ObservableProperty] private bool _isPlaylistPlaybackActive;
    [ObservableProperty] private bool _isStartupScreenOpen = true;
    [ObservableProperty] private bool _isNewProjectDialogOpen;
    [ObservableProperty] private bool _isExportDialogOpen;
    [ObservableProperty] private bool _isProjectPickerOpen;
    [ObservableProperty] private RecentProjectItemViewModel? _selectedRecentProject;
    [ObservableProperty] private PlaylistSummaryItemViewModel? _selectedPlaylist;
    [ObservableProperty] private PlaylistSummaryItemViewModel? _selectedTargetPlaylistForAdd;
    [ObservableProperty] private PlaylistClipItemViewModel? _selectedPlaylistItem;
    [ObservableProperty] private TagPreset? _selectedPreset;
    [ObservableProperty] private EventTypeItemViewModel? _selectedEventTypeItem;
    [ObservableProperty] private string _newProjectName = string.Empty;
    [ObservableProperty] private string _newProjectHomeTeam = string.Empty;
    [ObservableProperty] private string _newProjectAwayTeam = string.Empty;
    [ObservableProperty] private string _newProjectVideoPath = string.Empty;
    [ObservableProperty] private bool _newProjectBroadcastMode;
    [ObservableProperty] private string _eventTypeName = string.Empty;
    [ObservableProperty] private string _eventTypeHotkey = string.Empty;
    [ObservableProperty] private string _eventTypeColor = "#FFB300";
    [ObservableProperty] private string _eventTypeCategory = "Custom";
    [ObservableProperty] private string _eventTypeIconKey = "event";
    [ObservableProperty] private bool _eventTypeShowInStatistics = true;
    [ObservableProperty] private int _eventTypePreRollFrames;
    [ObservableProperty] private int _eventTypePostRollFrames;
    [ObservableProperty] private TagEventItemViewModel? _selectedTagEvent;
    [ObservableProperty] private AnnotationShapeType _selectedShapeType = AnnotationShapeType.Arrow;
    [ObservableProperty] private long _annotationStartFrame;
    [ObservableProperty] private long _annotationEndFrame = 1;
    [ObservableProperty] private double _annotationX1 = 100;
    [ObservableProperty] private double _annotationY1 = 100;
    [ObservableProperty] private double _annotationX2 = 260;
    [ObservableProperty] private double _annotationY2 = 160;
    [ObservableProperty] private string _annotationText = "Play";
    [ObservableProperty] private string _annotationColor = "#FFD700";
    [ObservableProperty] private bool _exportToCloud;
    [ObservableProperty] private string _exportOutputPath;
    [ObservableProperty] private string _exportFolderPath = string.Empty;
    [ObservableProperty] private bool _exportIncludeTacticalDrawings;
    [ObservableProperty] private bool _isExportInProgress;
    [ObservableProperty] private string _exportProgressText = "Подготовка к экспорту...";
    [ObservableProperty] private ExportSourceOption _selectedExportSource = ExportSourceOption.AllClips;
    [ObservableProperty] private ExportFormatOption _selectedExportFormat = ExportFormatOption.Mp4;
    [ObservableProperty] private ExportQualityOption _selectedExportQuality = ExportQualityOption.High4K;
    [ObservableProperty] private ExportDestinationOption _selectedExportDestination = ExportDestinationOption.Folder;
    [ObservableProperty] private string _yandexServiceUrl = "https://storage.yandexcloud.net";
    [ObservableProperty] private string _yandexBucket = string.Empty;
    [ObservableProperty] private string _yandexAccessKey = string.Empty;
    [ObservableProperty] private string _yandexSecretKey = string.Empty;
    [ObservableProperty] private string _yandexRegion = "ru-central1";
    [ObservableProperty] private string _yandexPrefix = "exports";

    partial void OnCurrentFrameChanged(long value)
    {
        OnPropertyChanged(nameof(CurrentTimeText));
        if (!IsBroadcastTimelineActive)
        {
            NotifyTimelineFrameChanged(refreshTimeline: false);
        }

        if (_ignoreFrameChange || DurationFrames <= 0 || IsLiveSource)
        {
            return;
        }

        SeekPlaybackToFrame(value);
    }

    partial void OnStatusMessageChanged(string value)
    {
        AppLogService.Status(value);
    }

    partial void OnSourceVideoPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanExportFromDialog));
    }

    partial void OnPlaybackVideoPathChanged(string value)
    {
        OnPropertyChanged(nameof(MediaPlayer));
    }

    partial void OnIsLiveSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(DurationTimeText));
    }

    partial void OnIsBroadcastModeProjectChanged(bool value)
    {
        if (!value)
        {
            _ = StopBroadcastRecordingAsync(openRecordedVideo: false);
            _ = StopBroadcastDvrAsync(CancellationToken.None);
            IsBroadcastRecording = false;
        }

        OnPropertyChanged(nameof(IsBroadcastPanelVisible));
        OnPropertyChanged(nameof(IsBroadcastEntityAvailable));
        OnPropertyChanged(nameof(CanUseBroadcastRecording));
    }

    partial void OnIsBroadcastTimelineActiveChanged(bool value)
    {
        _lastBroadcastTimelineLayoutRefreshFrame = -1;
        OnPropertyChanged(nameof(DurationTimeText));
        NotifyTimelineFrameChanged(refreshTimeline: true);
    }

    partial void OnFramesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(DurationTimeText));
        OnPropertyChanged(nameof(TagStartTimeText));
        OnPropertyChanged(nameof(TagEndTimeText));
        NotifyTimelineFrameChanged(refreshTimeline: true);
    }

    partial void OnDurationFramesChanged(long value)
    {
        OnPropertyChanged(nameof(DurationTimeText));
        if (!IsBroadcastTimelineActive)
        {
            NotifyTimelineFrameChanged(refreshTimeline: true);
        }
    }

    partial void OnBroadcastTimelineFrameChanged(long value)
    {
        if (IsBroadcastTimelineActive)
        {
            NotifyTimelineFrameChanged(refreshTimeline: false);
        }
    }

    private void NotifyTimelineFrameChanged(bool refreshTimeline)
    {
        OnPropertyChanged(nameof(TimelineCurrentFrame));
        OnPropertyChanged(nameof(TimelineDurationFrames));
        OnPropertyChanged(nameof(TimelineCurrentTimeText));
        OnPropertyChanged(nameof(TimelineDurationTimeText));
        OnPropertyChanged(nameof(TimelineHeaderTimeText));
        OnPropertyChanged(nameof(TimelineCanvasWidth));
        OnPropertyChanged(nameof(TimelineCurrentLineLeft));

        if (!refreshTimeline)
        {
            return;
        }

        RefreshTimelineTicks();
        RefreshTimelineRows();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlaybackButtonText));
        OnPropertyChanged(nameof(PlaybackGlyph));
        OnPropertyChanged(nameof(ShowOverlayPlayButton));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    partial void OnPlaybackRateChanged(double value)
    {
        var normalizedRate = Math.Clamp(value, 0.25d, 2.0d);
        if (Math.Abs(normalizedRate - value) > 0.0001d)
        {
            PlaybackRate = normalizedRate;
            return;
        }

        var shouldResumePlayback = _mediaPlaybackService.IsPlaying;
        _mediaPlaybackService.SetPlaybackRate(normalizedRate);
        if (shouldResumePlayback && !_mediaPlaybackService.IsPlaying)
        {
            _mediaPlaybackService.Play();
        }

        IsPlaying = shouldResumePlayback || _mediaPlaybackService.IsPlaying;
        OnPropertyChanged(nameof(PlaybackRateText));
    }

    partial void OnVideoZoomChanged(double value)
    {
        var normalizedZoom = NormalizeVideoZoom(value);
        if (Math.Abs(normalizedZoom - value) > 0.0001d)
        {
            VideoZoom = normalizedZoom;
            return;
        }

        OnPropertyChanged(nameof(VideoZoomText));
        OnPropertyChanged(nameof(CanZoomVideoIn));
        OnPropertyChanged(nameof(CanZoomVideoOut));
        ApplyVideoZoom();
    }

    partial void OnSelectedTargetPlaylistForAddChanged(PlaylistSummaryItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
    }

    partial void OnSelectedPresetChanged(TagPreset? value)
    {
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
        OnPropertyChanged(nameof(CanDeleteEditedPreset));

        if (value is null)
        {
            if (SelectedEventTypeItem is not null)
            {
                SelectedEventTypeItem = null;
            }
            return;
        }

        if (SelectedEventTypeItem?.Id != value.Id)
        {
            SelectedEventTypeItem = EventTypeItems.FirstOrDefault((item) => item.Id == value.Id);
        }

        EventTypeName = value.Name;
        EventTypeHotkey = value.Hotkey;
        EventTypeColor = value.ColorHex;
        EventTypeCategory = value.Category;
        EventTypeIconKey = value.IconKey;
        EventTypeShowInStatistics = value.ShowInStatistics;
        EventTypePreRollFrames = Math.Max(0, value.PreRollFrames);
        EventTypePostRollFrames = Math.Max(0, value.PostRollFrames);
    }

    partial void OnSelectedEventTypeItemChanged(EventTypeItemViewModel? value)
    {
        if (value is null || SelectedPreset?.Id == value.Id)
        {
            return;
        }

        SelectedPreset = value.Preset;
    }

    partial void OnEventTypeHotkeyChanged(string value)
    {
        if (_isAdjustingEventTypeHotkey)
        {
            return;
        }

        var normalizedHotkey = NormalizeSingleEnglishHotkey(value);
        var nextHotkey = normalizedHotkey ?? _lastValidEventTypeHotkey;

        if (normalizedHotkey is not null && HasHotkeyConflict(normalizedHotkey))
        {
            nextHotkey = _lastValidEventTypeHotkey;
            StatusMessage = $"Hotkey '{normalizedHotkey}' is already assigned to another event type.";
        }

        if (!string.Equals(value, nextHotkey, StringComparison.Ordinal))
        {
            _isAdjustingEventTypeHotkey = true;
            EventTypeHotkey = nextHotkey;
            _isAdjustingEventTypeHotkey = false;
            return;
        }

        _lastValidEventTypeHotkey = nextHotkey;
    }

    partial void OnSelectedTagEventChanged(TagEventItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanDeleteEditedTagEvent));
    }

    partial void OnTagStartFrameChanged(long value)
    {
        OnPropertyChanged(nameof(TagStartTimeText));
    }

    partial void OnTagEndFrameChanged(long value)
    {
        OnPropertyChanged(nameof(TagEndTimeText));
    }

    partial void OnSelectedRecentProjectChanged(RecentProjectItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanOpenSelectedRecentProject));
    }

    partial void OnSelectedPlaylistChanged(PlaylistSummaryItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanOpenSelectedPlaylist));
        OnPropertyChanged(nameof(CanDeleteSelectedPlaylist));
    }

    partial void OnSelectedEventsPanelTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsEventTypesTabSelected));
        OnPropertyChanged(nameof(IsEventsTabSelected));
    }

    partial void OnSelectedRightPanelTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsPlaylistsTabSelected));
        OnPropertyChanged(nameof(IsStatisticsTabSelected));
    }

    partial void OnIsPlayerPanelHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlayerPanelVisible));
    }

    partial void OnIsTimelineHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTimelineVisible));
    }

    partial void OnIsEventsPanelHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEventsPanelVisible));
    }

    partial void OnIsAnalysisPanelHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAnalysisPanelVisible));
    }

    partial void OnIsBroadcastPanelHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBroadcastPanelVisible));
    }

    partial void OnIsBroadcastRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleBroadcastLive));
        OnPropertyChanged(nameof(BroadcastRecordingButtonText));
        OnPropertyChanged(nameof(BroadcastRecordingGlyph));
        OnPropertyChanged(nameof(BroadcastRecordingStatusText));
    }

    partial void OnIsBroadcastDvrRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseBroadcastRecording));
        OnPropertyChanged(nameof(CanToggleBroadcastLive));
        OnPropertyChanged(nameof(BroadcastLiveButtonText));
        OnPropertyChanged(nameof(BroadcastLiveGlyph));
        OnPropertyChanged(nameof(BroadcastRecordingStatusText));
        OnPropertyChanged(nameof(CanExportFromDialog));
    }

    partial void OnIsBroadcastRecordingOperationInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseBroadcastRecording));
        OnPropertyChanged(nameof(CanToggleBroadcastLive));
    }

    partial void OnIsEditingPresetChanged(bool value)
    {
        OnPropertyChanged(nameof(PresetEditorTitle));
        OnPropertyChanged(nameof(CanDeleteEditedPreset));
    }

    partial void OnIsEditingTagEventChanged(bool value)
    {
        OnPropertyChanged(nameof(TagEventEditorTitle));
        OnPropertyChanged(nameof(CanDeleteEditedTagEvent));
    }

    partial void OnIsNewProjectDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlayerSurfaceVisible));
        OnPropertyChanged(nameof(IsStartupScreenVisible));
    }

    partial void OnIsExportDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlayerSurfaceVisible));
    }

    partial void OnIsStartupScreenOpenChanged(bool value)
    {
        if (!value)
        {
            IsProjectPickerOpen = false;
        }

        OnPropertyChanged(nameof(IsStartupScreenVisible));
        OnPropertyChanged(nameof(IsPlayerSurfaceVisible));
        OnPropertyChanged(nameof(CanCloseStartupScreen));
    }

    partial void OnNewProjectBroadcastModeChanged(bool value)
    {
        OnPropertyChanged(nameof(NewProjectVideoLabelText));
        OnPropertyChanged(nameof(NewProjectVideoPlaceholderText));
        OnPropertyChanged(nameof(NewProjectPrimaryButtonText));
    }

    partial void OnVolumeChanged(int value)
    {
        if (value > 0 && IsMuted)
        {
            _mediaPlaybackService.ToggleMute();
            IsMuted = false;
        }

        _mediaPlaybackService.SetVolume(value);
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    partial void OnSelectedExportSourceChanged(ExportSourceOption value)
    {
        OnPropertyChanged(nameof(IsExportAllClipsSelected));
        OnPropertyChanged(nameof(IsExportPlaylistSelected));
        OnPropertyChanged(nameof(IsExportFullMatchSelected));
        UpdateExportOutputPath();
    }

    partial void OnSelectedExportFormatChanged(ExportFormatOption value)
    {
        OnPropertyChanged(nameof(IsExportFormatMp4Selected));
        OnPropertyChanged(nameof(IsExportFormatAviSelected));
        OnPropertyChanged(nameof(IsExportFormatMovSelected));
        UpdateExportOutputPath();
    }

    partial void OnSelectedExportQualityChanged(ExportQualityOption value)
    {
        OnPropertyChanged(nameof(IsExportQualityLowSelected));
        OnPropertyChanged(nameof(IsExportQualityMediumSelected));
        OnPropertyChanged(nameof(IsExportQualityHighSelected));
    }

    partial void OnSelectedExportDestinationChanged(ExportDestinationOption value)
    {
        OnPropertyChanged(nameof(IsExportDestinationFolderSelected));
        OnPropertyChanged(nameof(IsExportDestinationTelegramSelected));
        OnPropertyChanged(nameof(IsExportDestinationBothSelected));
    }

    partial void OnExportFolderPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanExportFromDialog));
        UpdateExportOutputPath();
    }

    partial void OnIsExportInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExportFromDialog));
        OnPropertyChanged(nameof(CanCloseExportDialog));
        OnPropertyChanged(nameof(ExportPrimaryButtonText));
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        await RefreshRecentProjectsAsync(CancellationToken.None);
        ResetCurrentProjectState();

        IsStartupScreenOpen = true;
        StatusMessage = HasRecentProjects
            ? "Выберите проект для продолжения."
            : "Создайте проект, чтобы начать работу.";
    }

    [RelayCommand]
    private async Task ImportFromPathAsync()
    {
        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала создайте проект.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceVideoPath))
        {
            StatusMessage = "Select a video file path first.";
            return;
        }

        await ImportVideoAsync(SourceVideoPath);
    }

    public async Task ImportVideoAsync(string path)
    {
        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала создайте проект.";
            return;
        }

        try
        {
            var metadata = await _mediaPlaybackService.OpenAsync(path, CancellationToken.None);
            IsLiveSource = false;
            SourceVideoPath = metadata.FilePath;
            PlaybackVideoPath = metadata.FilePath;
            FramesPerSecond = metadata.FramesPerSecond;
            DurationFrames = metadata.DurationFrames;
            VideoWidth = metadata.Width;
            VideoHeight = metadata.Height;
            CurrentFrame = 0;
            IsPlaying = false;
            ResetVideoZoomState();
            RefreshPlaybackUiState();

            var mediaAsset = new MediaAsset(
                Guid.NewGuid(),
                _projectId,
                metadata.FilePath,
                metadata.FramesPerSecond,
                metadata.DurationFrames,
                metadata.Width,
                metadata.Height,
                DateTimeOffset.UtcNow);

            await _repository.UpsertMediaAssetAsync(mediaAsset, CancellationToken.None);
            StatusMessage = "Video imported.";
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Import video failed");
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenLiveCameraAsync()
    {
        try
        {
            StatusMessage = "Открываем камеру...";
            var metadata = await _mediaPlaybackService.OpenLiveCameraAsync(null, CancellationToken.None);
            IsLiveSource = true;
            SourceVideoPath = metadata.FilePath;
            PlaybackVideoPath = metadata.FilePath;
            FramesPerSecond = metadata.FramesPerSecond;
            DurationFrames = Math.Max(1, metadata.DurationFrames);
            VideoWidth = metadata.Width;
            VideoHeight = metadata.Height;
            CurrentFrame = 0;
            PlaybackRate = 1d;
            IsPlaying = true;
            IsStartupScreenOpen = false;
            ResetVideoZoomState();
            RefreshPlaybackUiState();
            _mediaPlaybackService.Play();
            StatusMessage = $"Live-камера запущена: {metadata.FilePath.Replace("camera://", string.Empty, StringComparison.Ordinal)}";
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Open live camera failed");
            StatusMessage = $"Не удалось открыть камеру: {ex.Message}";
        }
    }

    public void StartBroadcastTimeline(DateTimeOffset? startedAtUtc = null)
    {
        if (!IsBroadcastModeProject)
        {
            return;
        }

        FramesPerSecond = 30;
        if (startedAtUtc.HasValue && BroadcastTimelineFrame <= 0)
        {
            _broadcastTimelineStartedAtUtc = startedAtUtc.Value;
            _broadcastTimelineOffsetFrame = 0;
        }
        else
        {
            _broadcastTimelineStartedAtUtc = DateTimeOffset.UtcNow;
            _broadcastTimelineOffsetFrame = Math.Max(0, BroadcastTimelineFrame);
        }

        IsBroadcastTimelineActive = true;
        _lastBroadcastTimelineLayoutRefreshFrame = -1;
        _broadcastTimelineTimer.Start();
        UpdateBroadcastTimeline();
    }

    public void StopBroadcastTimeline()
    {
        UpdateBroadcastTimeline();
        _broadcastTimelineOffsetFrame = Math.Max(0, BroadcastTimelineFrame);
        _broadcastTimelineTimer.Stop();
        IsBroadcastTimelineActive = false;
    }

    public async Task<string> EnsureBroadcastDvrAsync(CancellationToken cancellationToken)
    {
        if (!IsBroadcastModeProject)
        {
            throw new InvalidOperationException("Откройте проект трансляции.");
        }

        if (string.IsNullOrWhiteSpace(_projectFolderPath))
        {
            throw new InvalidOperationException("Сначала откройте проект трансляции.");
        }

        if (IsBroadcastDvrRunning
            && !string.IsNullOrWhiteSpace(BroadcastDvrPreviewSource)
            && _broadcastDvrService.IsRunning)
        {
            return BroadcastDvrPreviewSource;
        }

        var session = await _broadcastDvrService.StartAsync(_projectFolderPath, cameraName: null, cancellationToken);

        BroadcastDvrPreviewSource = session.PreviewSource;
        IsBroadcastDvrRunning = true;
        FramesPerSecond = session.FramesPerSecond;
        StartBroadcastTimeline(session.StartedAtUtc);
        StatusMessage = "Live-DVR запущен: трансляция пишется сегментами.";
        return session.PreviewSource;
    }

    public async Task StopBroadcastDvrAsync(CancellationToken cancellationToken)
    {
        await _broadcastDvrService.StopAsync(cancellationToken);
        BroadcastDvrPreviewSource = string.Empty;
        IsBroadcastDvrRunning = false;
        IsBroadcastRecording = false;
        BroadcastRecordingPreviewSource = string.Empty;
        StopBroadcastTimeline();
    }

    [RelayCommand]
    private async Task ToggleBroadcastRecordingAsync()
    {
        if (!IsBroadcastModeProject)
        {
            return;
        }

        if (IsBroadcastRecording)
        {
            await StopBroadcastRecordingAsync(openRecordedVideo: true);
            return;
        }

        await StartBroadcastRecordingAsync();
    }

    private async Task StartBroadcastRecordingAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectFolderPath))
        {
            StatusMessage = "Сначала откройте проект трансляции.";
            return;
        }

        IsBroadcastRecordingOperationInProgress = true;
        try
        {
            if (!IsBroadcastDvrRunning || !_broadcastDvrService.IsRunning)
            {
                await EnsureBroadcastDvrAsync(CancellationToken.None);
            }

            UpdateBroadcastTimeline();
            _broadcastRecordingStartFrame = Math.Max(0, TimelineCurrentFrame);
            SyncPlayerToBroadcastLiveEdge();
            IsBroadcastRecording = true;
            StatusMessage = $"Начата отметка фрагмента с {TimelineCurrentTimeText}.";
        }
        catch (Exception ex)
        {
            IsBroadcastRecording = false;
            BroadcastRecordingPreviewSource = string.Empty;
            AppLogService.Error(ex, "Start broadcast recording failed");
            StatusMessage = $"Не удалось начать запись трансляции: {ex.Message}";
        }
        finally
        {
            IsBroadcastRecordingOperationInProgress = false;
        }
    }

    private async Task StopBroadcastRecordingAsync(bool openRecordedVideo)
    {
        if (!IsBroadcastRecording)
        {
            return;
        }

        IsBroadcastRecordingOperationInProgress = true;
        try
        {
            UpdateBroadcastTimeline();
            var endFrame = Math.Max(_broadcastRecordingStartFrame, TimelineCurrentFrame);
            var startFrame = Math.Min(_broadcastRecordingStartFrame, endFrame);
            IsBroadcastRecording = false;
            BroadcastRecordingPreviewSource = string.Empty;

            if (openRecordedVideo
                && IsBroadcastDvrRunning
                && _broadcastDvrService.IsRunning)
            {
                var outputFolderPath = Path.Combine(_projectFolderPath, "media", "broadcast");
                Directory.CreateDirectory(outputFolderPath);
                var recordedPath = Path.Combine(outputFolderPath, $"broadcast-clip-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.mp4");
                StatusMessage = "Завершаем клип из live-DVR...";
                _currentBroadcastRecordingPath = await _broadcastDvrService.ExportAvailableRangeAsync(
                    startFrame,
                    endFrame,
                    FramesPerSecond,
                    recordedPath,
                    CancellationToken.None);

                await OpenBroadcastRecordingInPlayerAsync(recordedPath, startFrame);
                StatusMessage = $"Запись открыта в плеере: {Path.GetFileName(recordedPath)}";
                return;
            }

            StatusMessage = "Отметка фрагмента остановлена.";
        }
        catch (Exception ex)
        {
            IsBroadcastRecording = false;
            BroadcastRecordingPreviewSource = string.Empty;
            AppLogService.Error(ex, "Stop broadcast recording failed");
            StatusMessage = $"Не удалось остановить запись трансляции: {ex.Message}";
        }
        finally
        {
            IsBroadcastRecordingOperationInProgress = false;
        }
    }

    private void SyncPlayerToBroadcastLiveEdge()
    {
        if (IsBroadcastTimelineActive)
        {
            UpdateBroadcastTimeline();
        }
        else
        {
            StartBroadcastTimeline();
        }

        _mediaPlaybackService.Pause();
        _currentBroadcastRecordingPath = null;
        _currentBroadcastRecordingStartFrame = 0;
        IsLiveSource = true;
        SourceVideoPath = "camera://broadcast";
        PlaybackVideoPath = SourceVideoPath;
        PlaybackRate = 1d;
        _ignoreFrameChange = true;
        DurationFrames = Math.Max(1, TimelineDurationFrames);
        CurrentFrame = Math.Max(0, TimelineCurrentFrame);
        _ignoreFrameChange = false;
        IsPlaying = false;
        ResetVideoZoomState();
        RefreshPlaybackUiState();
    }

    private async Task OpenBroadcastRecordingInPlayerAsync(string recordedPath, long broadcastStartFrame)
    {
        var metadata = await _mediaPlaybackService.OpenAsync(recordedPath, CancellationToken.None);
        _currentBroadcastRecordingPath = recordedPath;
        _currentBroadcastRecordingStartFrame = Math.Max(0, broadcastStartFrame);
        IsLiveSource = false;
        SourceVideoPath = metadata.FilePath;
        PlaybackVideoPath = metadata.FilePath;
        FramesPerSecond = metadata.FramesPerSecond;
        DurationFrames = Math.Max(1, metadata.DurationFrames);
        VideoWidth = metadata.Width;
        VideoHeight = metadata.Height;
        _ignoreFrameChange = true;
        CurrentFrame = 0;
        _ignoreFrameChange = false;
        PlaybackRate = 1d;
        IsPlaying = false;
        ResetVideoZoomState();
        RefreshPlaybackUiState();
    }

    private void UpdateBroadcastTimeline()
    {
        if (!IsBroadcastTimelineActive)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _broadcastTimelineStartedAtUtc;
        var frame = _broadcastTimelineOffsetFrame
            + Math.Max(0, (long)Math.Round(elapsed.TotalSeconds * Math.Max(1d, FramesPerSecond)));

        BroadcastTimelineFrame = frame;
        RefreshBroadcastTimelineLayoutIfNeeded(frame);

        if (IsLiveSource || string.Equals(SourceVideoPath, "camera://broadcast", StringComparison.OrdinalIgnoreCase))
        {
            _ignoreFrameChange = true;
            DurationFrames = Math.Max(1, frame);
            CurrentFrame = frame;
            _ignoreFrameChange = false;
        }
    }

    private void RefreshBroadcastTimelineLayoutIfNeeded(long frame)
    {
        var framesPerRefresh = Math.Max(1L, (long)Math.Round(Math.Max(1d, FramesPerSecond)));
        if (_lastBroadcastTimelineLayoutRefreshFrame >= 0
            && frame >= _lastBroadcastTimelineLayoutRefreshFrame
            && frame - _lastBroadcastTimelineLayoutRefreshFrame < framesPerRefresh)
        {
            return;
        }

        _lastBroadcastTimelineLayoutRefreshFrame = frame;
        RefreshTimelineTicks();
        RefreshTimelineRows();
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_mediaPlaybackService.IsPlaying)
        {
            _mediaPlaybackService.Pause();
        }
        else
        {
            _mediaPlaybackService.Play();
        }
    }

    [RelayCommand] private void StepForward() => _mediaPlaybackService.StepFrameForward();
    [RelayCommand] private void StepBackward() => _mediaPlaybackService.StepFrameBackward();

    [RelayCommand]
    private void SeekBackwardFiveSeconds() => SeekBySeconds(-5d);

    [RelayCommand]
    private void SeekForwardFiveSeconds() => SeekBySeconds(5d);

    [RelayCommand]
    private void SeekBackwardOneSecond() => SeekBySeconds(-1d);

    [RelayCommand]
    private void SeekForwardOneSecond() => SeekBySeconds(1d);

    [RelayCommand]
    private void SeekBackwardTenSeconds() => SeekBySeconds(-10d);

    [RelayCommand]
    private void SeekForwardTenSeconds() => SeekBySeconds(10d);

    private void SeekBySeconds(double seconds)
    {
        var offsetFrames = (long)Math.Round(Math.Max(1d, FramesPerSecond) * seconds);
        CurrentFrame = Math.Clamp(CurrentFrame + offsetFrames, 0, Math.Max(0, DurationFrames));
    }

    public void SetVideoZoomFocus(double normalizedX, double normalizedY)
    {
        (_videoZoomCenterX, _videoZoomCenterY) = ClampVideoZoomCenter(normalizedX, normalizedY, VideoZoom);
        NotifyVideoZoomCenterChanged();
        ApplyVideoZoom();
    }

    public void ZoomVideo(double wheelDelta) => ZoomVideo(wheelDelta, _videoZoomCenterX, _videoZoomCenterY);

    public void ZoomVideo(double wheelDelta, double normalizedAnchorX, double normalizedAnchorY)
    {
        if (Math.Abs(wheelDelta) < 0.001d)
        {
            return;
        }

        var currentZoom = VideoZoom;
        var zoomStep = wheelDelta < 0 ? VideoZoomOutStep : VideoZoomStep;
        var nextZoom = NormalizeVideoZoom(currentZoom + Math.Sign(wheelDelta) * zoomStep);
        var anchorX = Math.Clamp(normalizedAnchorX, 0d, 1d);
        var anchorY = Math.Clamp(normalizedAnchorY, 0d, 1d);
        var oldVisibleSize = 1d / currentZoom;
        var oldLeft = _videoZoomCenterX - (oldVisibleSize / 2d);
        var oldTop = _videoZoomCenterY - (oldVisibleSize / 2d);
        var anchoredVideoX = oldLeft + (anchorX * oldVisibleSize);
        var anchoredVideoY = oldTop + (anchorY * oldVisibleSize);
        var nextVisibleSize = 1d / nextZoom;
        var nextCenterX = anchoredVideoX - (anchorX * nextVisibleSize) + (nextVisibleSize / 2d);
        var nextCenterY = anchoredVideoY - (anchorY * nextVisibleSize) + (nextVisibleSize / 2d);
        (_videoZoomCenterX, _videoZoomCenterY) = ClampVideoZoomCenter(nextCenterX, nextCenterY, nextZoom);
        NotifyVideoZoomCenterChanged();
        SetVideoZoom(nextZoom);
    }

    public void SetVideoZoomLevel(double zoom, double normalizedAnchorX, double normalizedAnchorY)
    {
        var currentZoom = VideoZoom;
        var nextZoom = ClampVideoZoom(zoom);
        var anchorX = Math.Clamp(normalizedAnchorX, 0d, 1d);
        var anchorY = Math.Clamp(normalizedAnchorY, 0d, 1d);
        var oldVisibleSize = 1d / currentZoom;
        var oldLeft = _videoZoomCenterX - (oldVisibleSize / 2d);
        var oldTop = _videoZoomCenterY - (oldVisibleSize / 2d);
        var anchoredVideoX = oldLeft + (anchorX * oldVisibleSize);
        var anchoredVideoY = oldTop + (anchorY * oldVisibleSize);
        var nextVisibleSize = 1d / nextZoom;
        var nextCenterX = anchoredVideoX - (anchorX * nextVisibleSize) + (nextVisibleSize / 2d);
        var nextCenterY = anchoredVideoY - (anchorY * nextVisibleSize) + (nextVisibleSize / 2d);
        (_videoZoomCenterX, _videoZoomCenterY) = ClampVideoZoomCenter(nextCenterX, nextCenterY, nextZoom);
        NotifyVideoZoomCenterChanged();
        SetVideoZoomCore(nextZoom);
    }

    public void PanVideoZoom(double normalizedDeltaX, double normalizedDeltaY)
    {
        if (VideoZoom <= VideoZoomMinimum + 0.001d)
        {
            return;
        }

        SetVideoZoomFocus(
            _videoZoomCenterX - (normalizedDeltaX / VideoZoom),
            _videoZoomCenterY - (normalizedDeltaY / VideoZoom));
    }

    [RelayCommand]
    private void ZoomVideoIn() => SetVideoZoom(VideoZoom + VideoZoomStep);

    [RelayCommand]
    private void ZoomVideoOut() => SetVideoZoom(VideoZoom - VideoZoomStep);

    [RelayCommand]
    private void ResetVideoZoom() => SetVideoZoom(VideoZoomMinimum);

    private void SetVideoZoom(double zoom)
    {
        var normalizedZoom = NormalizeVideoZoom(zoom);
        SetVideoZoomCore(normalizedZoom);
    }

    private void SetVideoZoomCore(double normalizedZoom)
    {
        (_videoZoomCenterX, _videoZoomCenterY) = ClampVideoZoomCenter(_videoZoomCenterX, _videoZoomCenterY, normalizedZoom);
        NotifyVideoZoomCenterChanged();
        if (Math.Abs(VideoZoom - normalizedZoom) < 0.001d)
        {
            ApplyVideoZoom();
            return;
        }

        VideoZoom = normalizedZoom;
    }

    private void NotifyVideoZoomCenterChanged()
    {
        OnPropertyChanged(nameof(VideoZoomCenterX));
        OnPropertyChanged(nameof(VideoZoomCenterY));
    }

    private void ResetVideoZoomState()
    {
        _videoZoomCenterX = 0.5d;
        _videoZoomCenterY = 0.5d;
        SetVideoZoom(VideoZoomMinimum);
    }

    private void ApplyVideoZoom()
    {
        _mediaPlaybackService.SetVideoZoom(VideoZoom, _videoZoomCenterX, _videoZoomCenterY, _videoViewportWidth, _videoViewportHeight);
    }

    private static (double X, double Y) ClampVideoZoomCenter(double centerX, double centerY, double zoom)
    {
        var visibleSize = 1d / Math.Max(VideoZoomMinimum, zoom);
        var minimum = visibleSize / 2d;
        var maximum = 1d - minimum;
        return (
            Math.Clamp(centerX, minimum, maximum),
            Math.Clamp(centerY, minimum, maximum));
    }

    private static double NormalizeVideoZoom(double zoom)
    {
        var clampedZoom = ClampVideoZoom(zoom);
        return Math.Round(clampedZoom / VideoZoomStep, MidpointRounding.AwayFromZero) * VideoZoomStep;
    }

    private static double ClampVideoZoom(double zoom) => Math.Clamp(zoom, VideoZoomMinimum, VideoZoomMaximum);

    public void PreviewSeekFrame(long frame) => PreviewPlayerSeekFrame(frame);

    public void CommitSeekFrame(long frame) => CommitPlayerSeekFrame(frame);

    public void PreviewPlayerSeekFrame(long frame)
    {
        var safeFrame = Math.Clamp(frame, 0, Math.Max(0, DurationFrames));
        _ignoreFrameChange = true;
        CurrentFrame = safeFrame;
        _ignoreFrameChange = false;
    }

    public void CommitPlayerSeekFrame(long frame)
    {
        var safeFrame = Math.Clamp(frame, 0, Math.Max(0, DurationFrames));
        if (CurrentFrame != safeFrame)
        {
            CurrentFrame = safeFrame;
            return;
        }

        SeekPlaybackToFrame(safeFrame);
    }

    public void PreviewTimelineSeekFrame(long frame)
    {
        if (IsBroadcastTimelineActive)
        {
            return;
        }

        PreviewPlayerSeekFrame(frame);
    }

    public void CommitTimelineSeekFrame(long frame)
    {
        if (IsBroadcastTimelineActive)
        {
            return;
        }

        CommitPlayerSeekFrame(frame);
    }

    private void SeekPlaybackToFrame(long frame)
    {
        var shouldResumePlayback = _mediaPlaybackService.IsPlaying;
        _mediaPlaybackService.SeekToFrame(frame);
        if (shouldResumePlayback && !_mediaPlaybackService.IsPlaying)
        {
            _mediaPlaybackService.Play();
        }

        IsPlaying = shouldResumePlayback || _mediaPlaybackService.IsPlaying;
    }

    [RelayCommand]
    private void SetPlaybackRate(string? playbackRateText)
    {
        if (string.IsNullOrWhiteSpace(playbackRateText))
        {
            return;
        }

        var normalizedText = playbackRateText.Trim().Replace("x", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!double.TryParse(normalizedText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var playbackRate))
        {
            return;
        }

        PlaybackRate = playbackRate;
    }

    [RelayCommand]
    private void ToggleMute()
    {
        var nextMutedState = !IsMuted;
        _mediaPlaybackService.ToggleMute();
        IsMuted = nextMutedState;
        Volume = _mediaPlaybackService.Volume;
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    [RelayCommand]
    private void SelectEventsPanelTab(string tabKey)
    {
        SelectedEventsPanelTab = string.IsNullOrWhiteSpace(tabKey) ? "EventTypes" : tabKey;
    }

    [RelayCommand]
    private void SelectRightPanelTab(string tabKey)
    {
        SelectedRightPanelTab = string.IsNullOrWhiteSpace(tabKey) ? "Playlists" : tabKey;
    }

    [RelayCommand]
    private void OpenExportDialog()
    {
        OpenExportDialogCore(_activePlaylistSegments.Count > 0 ? ExportSourceOption.Playlist : ExportSourceOption.AllClips);
    }

    [RelayCommand]
    private void OpenExportDialogForPlaylist()
    {
        OpenExportDialogCore(ExportSourceOption.Playlist);
    }

    [RelayCommand]
    private void CloseExportDialog()
    {
        if (IsExportInProgress)
        {
            return;
        }

        IsExportDialogOpen = false;
    }

    [RelayCommand]
    private void SelectExportSource(string? value)
    {
        if (Enum.TryParse<ExportSourceOption>(value, true, out var parsed))
        {
            SelectedExportSource = parsed;
        }
    }

    [RelayCommand]
    private void SelectExportFormat(string? value)
    {
        if (Enum.TryParse<ExportFormatOption>(value, true, out var parsed))
        {
            SelectedExportFormat = parsed;
        }
    }

    [RelayCommand]
    private void SelectExportQuality(string? value)
    {
        if (Enum.TryParse<ExportQualityOption>(value, true, out var parsed))
        {
            SelectedExportQuality = parsed;
        }
    }

    [RelayCommand]
    private void SelectExportDestination(string? value)
    {
        if (Enum.TryParse<ExportDestinationOption>(value, true, out var parsed))
        {
            SelectedExportDestination = parsed;
        }
    }

    [RelayCommand]
    private void ToggleTimelineVisibility()
    {
        IsTimelineHidden = !IsTimelineHidden;
    }

    [RelayCommand]
    private void ToggleEventsPanelVisibility()
    {
        IsEventsPanelHidden = !IsEventsPanelHidden;
    }

    [RelayCommand]
    private void ToggleAnalysisPanelVisibility()
    {
        IsAnalysisPanelHidden = !IsAnalysisPanelHidden;
    }

    public async Task HandleEventTypeHotkeyAsync(string hotkey)
    {
        if (_projectId == Guid.Empty || string.IsNullOrWhiteSpace(hotkey))
        {
            return;
        }

        var normalizedHotkey = hotkey.Trim().ToUpperInvariant();
        var preset = TagPresets.FirstOrDefault((candidate) =>
            string.Equals(candidate.Hotkey?.Trim(), normalizedHotkey, StringComparison.OrdinalIgnoreCase));

        if (preset is null)
        {
            return;
        }

        SelectedEventsPanelTab = "Events";
        var taggingFrame = GetCurrentTaggingFrame();

        if (IsTagEventEditorOpen)
        {
            var matchesSelectedPreset =
                SelectedPreset is not null &&
                string.Equals(SelectedPreset.Hotkey?.Trim(), normalizedHotkey, StringComparison.OrdinalIgnoreCase);

            if (matchesSelectedPreset)
            {
                TagEndFrame = Math.Max(TagStartFrame, taggingFrame);
                await AddTagAsync();
                return;
            }

            SelectedPreset = preset;
            StatusMessage = $"Event type switched to '{preset.Name}'.";
            return;
        }

        SelectedPreset = preset;
        SelectedTagEvent = null;
        IsEditingTagEvent = false;
        TagStartFrame = Math.Max(0, taggingFrame - Math.Max(0, preset.PreRollFrames));
        TagEndFrame = taggingFrame;
        TagPlayer = string.Empty;
        TagPeriod = string.Empty;
        TagNotes = string.Empty;
        if (TagTeamSide is TeamSide.Unknown or TeamSide.Neutral)
        {
            TagTeamSide = TeamSide.Home;
        }

        IsTagEventEditorOpen = true;
        StatusMessage = $"New '{preset.Name}' event started.";
    }

    private long GetCurrentTaggingFrame()
    {
        if (IsBroadcastTimelineActive)
        {
            UpdateBroadcastTimeline();
            return TimelineCurrentFrame;
        }

        if (TryGetBroadcastRecordingPlaybackFrame(out var broadcastFrame))
        {
            return broadcastFrame;
        }

        return CurrentFrame;
    }

    private bool TryGetBroadcastRecordingPlaybackFrame(out long broadcastFrame)
    {
        broadcastFrame = 0;
        if (!IsBroadcastModeProject
            || IsLiveSource
            || string.IsNullOrWhiteSpace(_currentBroadcastRecordingPath)
            || string.IsNullOrWhiteSpace(SourceVideoPath)
            || !PathsEqual(_currentBroadcastRecordingPath, SourceVideoPath))
        {
            return false;
        }

        broadcastFrame = Math.Max(0, _currentBroadcastRecordingStartFrame + CurrentFrame);
        return true;
    }

    private void ResetPresetEditorFields()
    {
        if (IsEditingPreset && SelectedPreset is not null)
        {
            EventTypeName = SelectedPreset.Name;
            EventTypeHotkey = SelectedPreset.Hotkey;
            EventTypeColor = SelectedPreset.ColorHex;
            EventTypeCategory = SelectedPreset.Category;
            EventTypeIconKey = SelectedPreset.IconKey;
            EventTypeShowInStatistics = SelectedPreset.ShowInStatistics;
            EventTypePreRollFrames = Math.Max(0, SelectedPreset.PreRollFrames);
            EventTypePostRollFrames = Math.Max(0, SelectedPreset.PostRollFrames);
            return;
        }

        EventTypeName = string.Empty;
        EventTypeHotkey = string.Empty;
        EventTypeColor = "#FFB300";
        EventTypeCategory = "Custom";
        EventTypeIconKey = "event";
        EventTypeShowInStatistics = true;
        EventTypePreRollFrames = 0;
        EventTypePostRollFrames = 0;
    }
    [RelayCommand]
    private void OpenNewPresetEditor()
    {
        IsEditingPreset = false;
        SelectedPreset = null;
        ResetPresetEditorFields();
        IsPresetEditorOpen = true;
    }

    [RelayCommand]
    private void ClosePresetEditor()
    {
        ResetPresetEditorFields();
        IsPresetEditorOpen = false;
    }

    [RelayCommand]
    private void OpenNewTagEventEditor()
    {
        IsEditingTagEvent = false;
        SelectedTagEvent = null;
        if (SelectedPreset is null)
        {
            SelectedPreset = TagPresets.FirstOrDefault();
        }

        var preRollFrames = Math.Max(0, SelectedPreset?.PreRollFrames ?? 0);
        var taggingFrame = GetCurrentTaggingFrame();
        TagStartFrame = Math.Max(0, taggingFrame - preRollFrames);
        TagEndFrame = taggingFrame;
        TagTeamSide = TeamSide.Home;
        TagPlayer = string.Empty;
        TagPeriod = string.Empty;
        TagNotes = string.Empty;
        IsTagEventEditorOpen = true;
    }

    [RelayCommand]
    private void CloseTagEventEditor()
    {
        IsTagEventEditorOpen = false;
    }

    [RelayCommand]
    private async Task OpenStartupScreenAsync()
    {
        await RefreshRecentProjectsAsync(CancellationToken.None);
        IsStartupScreenOpen = true;
        StatusMessage = HasRecentProjects
            ? "Выберите проект для продолжения."
            : "Создайте проект, чтобы начать работу.";
    }

    [RelayCommand]
    private void CloseStartupScreen()
    {
        if (_projectId == Guid.Empty)
        {
            return;
        }

        IsStartupScreenOpen = false;
    }

    [RelayCommand]
    private async Task OpenProjectPickerAsync()
    {
        await RefreshRecentProjectsAsync(CancellationToken.None);
        SelectedRecentProject = ProjectPickerItems.FirstOrDefault((item) => item.ProjectId == _projectId)
            ?? ProjectPickerItems.FirstOrDefault();
        IsProjectPickerOpen = true;
        StatusMessage = HasProjectPickerItems
            ? "Выберите проект из списка."
            : "Проектов пока нет.";
    }

    [RelayCommand]
    private void CloseProjectPicker()
    {
        IsProjectPickerOpen = false;
    }

    [RelayCommand]
    private async Task OpenSelectedRecentProjectAsync()
    {
        if (SelectedRecentProject is null && ProjectPickerItems.Count > 0)
        {
            SelectedRecentProject = ProjectPickerItems[0];
        }

        if (SelectedRecentProject is null)
        {
            StatusMessage = HasRecentProjects
                ? "Сначала выберите проект."
                : "Проектов пока нет.";
            return;
        }

        await OpenRecentProjectAsync(SelectedRecentProject);
    }

    [RelayCommand]
    private async Task OpenRecentProjectAsync(RecentProjectItemViewModel? recentProject)
    {
        if (recentProject is null)
        {
            return;
        }

        var project = await _repository.GetProjectAsync(recentProject.ProjectId, CancellationToken.None);
        if (project is null)
        {
            StatusMessage = "The selected project could not be found.";
            await RefreshRecentProjectsAsync(CancellationToken.None);
            return;
        }

        await LoadProjectAsync(project, CancellationToken.None);
        IsProjectPickerOpen = false;
        IsStartupScreenOpen = false;
        StatusMessage = $"Project '{project.Name}' opened.";
    }

    [RelayCommand]
    private void OpenNewProjectDialog()
    {
        NewProjectName = string.Empty;
        NewProjectHomeTeam = string.Empty;
        NewProjectAwayTeam = string.Empty;
        NewProjectVideoPath = string.Empty;
        NewProjectBroadcastMode = false;
        IsProjectPickerOpen = false;
        IsNewProjectDialogOpen = true;
    }

    [RelayCommand]
    private void CloseNewProjectDialog()
    {
        IsNewProjectDialogOpen = false;
    }

    [RelayCommand]
    private async Task ContinueNewProjectLegacyAsync()
    {
        StatusMessage = "Переход к импорту видео пока не реализован.";
        IsNewProjectDialogOpen = false;
    }

    [RelayCommand]
    private async Task ContinueNewProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            StatusMessage = "Project name is required.";
            return;
        }

        if (!NewProjectBroadcastMode && string.IsNullOrWhiteSpace(NewProjectVideoPath))
        {
            StatusMessage = "Select a video file.";
            return;
        }

        try
        {
            var result = await _projectSetupService.CreateProjectWithVideoAsync(
                new CreateProjectRequestDto(
                    NewProjectName.Trim(),
                    NewProjectVideoPath.Trim(),
                    Description: null,
                    HomeTeamName: string.IsNullOrWhiteSpace(NewProjectHomeTeam) ? null : NewProjectHomeTeam.Trim(),
                    AwayTeamName: string.IsNullOrWhiteSpace(NewProjectAwayTeam) ? null : NewProjectAwayTeam.Trim(),
                    MoveVideoToProjectFolder: false,
                    IsBroadcastMode: NewProjectBroadcastMode),
                CancellationToken.None);

            var project = await _repository.GetProjectAsync(result.ProjectId, CancellationToken.None)
                ?? throw new InvalidOperationException("Created project could not be loaded.");

            await LoadProjectAsync(project, CancellationToken.None);
            await RefreshRecentProjectsAsync(CancellationToken.None);
            SelectedRecentProject = RecentProjects.FirstOrDefault((item) => item.ProjectId == project.Id);
            IsStartupScreenOpen = false;
            IsNewProjectDialogOpen = false;
            StatusMessage = $"Project '{project.Name}' created.";
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Create project failed");
            StatusMessage = $"Project creation failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddPresetAsync()
    {
        var preset = new TagPreset(
            Guid.NewGuid(),
            _projectId,
            string.IsNullOrWhiteSpace(EventTypeName) ? $"Custom {TagPresets.Count + 1}" : EventTypeName.Trim(),
            string.IsNullOrWhiteSpace(EventTypeColor) ? "#FFB300" : EventTypeColor.Trim(),
            string.IsNullOrWhiteSpace(EventTypeCategory) ? "Custom" : EventTypeCategory.Trim(),
            false,
            string.IsNullOrWhiteSpace(EventTypeHotkey) ? string.Empty : EventTypeHotkey.Trim(),
            string.IsNullOrWhiteSpace(EventTypeIconKey) ? "event" : EventTypeIconKey.Trim(),
            EventTypeShowInStatistics,
            Math.Max(0, EventTypePreRollFrames),
            Math.Max(0, EventTypePostRollFrames));

        await _repository.UpsertTagPresetAsync(preset, CancellationToken.None);
        TagPresets.Add(preset);
        SelectedPreset = preset;
        IsEditingPreset = true;
        IsPresetEditorOpen = false;
        await RefreshTagsAsync();
        StatusMessage = $"Preset '{preset.Name}' added.";
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (!IsEditingPreset)
        {
            await AddPresetAsync();
            return;
        }

        if (SelectedPreset is null)
        {
            StatusMessage = "Select an event type first.";
            return;
        }

        var updatedPreset = SelectedPreset with
        {
            Name = string.IsNullOrWhiteSpace(EventTypeName) ? SelectedPreset.Name : EventTypeName.Trim(),
            Hotkey = string.IsNullOrWhiteSpace(EventTypeHotkey) ? string.Empty : EventTypeHotkey.Trim(),
            ColorHex = string.IsNullOrWhiteSpace(EventTypeColor) ? SelectedPreset.ColorHex : EventTypeColor.Trim(),
            Category = string.IsNullOrWhiteSpace(EventTypeCategory) ? "Custom" : EventTypeCategory.Trim(),
            IconKey = string.IsNullOrWhiteSpace(EventTypeIconKey) ? "event" : EventTypeIconKey.Trim(),
            ShowInStatistics = EventTypeShowInStatistics,
            PreRollFrames = Math.Max(0, EventTypePreRollFrames),
            PostRollFrames = Math.Max(0, EventTypePostRollFrames)
        };

        await _repository.UpsertTagPresetAsync(updatedPreset, CancellationToken.None);

        var selectedIndex = TagPresets.IndexOf(SelectedPreset);
        if (selectedIndex >= 0)
        {
            TagPresets[selectedIndex] = updatedPreset;
        }

        SelectedPreset = updatedPreset;
        IsPresetEditorOpen = false;
        await RefreshTagsAsync();
        StatusMessage = $"Preset '{updatedPreset.Name}' updated.";
    }

    [RelayCommand]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is null)
        {
            StatusMessage = "Select an event type first.";
            return;
        }

        var presetToDelete = SelectedPreset;
        await _repository.DeleteTagPresetAsync(_projectId, presetToDelete.Id, CancellationToken.None);
        TagPresets.Remove(presetToDelete);
        SelectedPreset = TagPresets.FirstOrDefault();
        IsPresetEditorOpen = false;
        IsEditingPreset = false;
        await RefreshTagsAsync();
        StatusMessage = $"Preset '{presetToDelete.Name}' deleted.";
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (SelectedPreset is null)
        {
            StatusMessage = "Select a tag preset.";
            return;
        }

        var eventId = IsEditingTagEvent && SelectedTagEvent is not null
            ? SelectedTagEvent.Id
            : Guid.NewGuid();

        var effectiveEndFrame = Math.Max(
            TagStartFrame,
            TagEndFrame + (!IsEditingTagEvent ? Math.Max(0, SelectedPreset.PostRollFrames) : 0));

        var tagEvent = new TagEvent(
            eventId,
            _projectId,
            SelectedPreset.Id,
            Math.Max(0, TagStartFrame),
            effectiveEndFrame,
            string.IsNullOrWhiteSpace(TagPlayer) ? null : TagPlayer,
            string.IsNullOrWhiteSpace(TagPeriod) ? null : TagPeriod,
            string.IsNullOrWhiteSpace(TagNotes) ? null : TagNotes,
            DateTimeOffset.UtcNow,
            TagTeamSide);

        _tagService.Validate(tagEvent);
        await _repository.UpsertTagEventAsync(tagEvent, CancellationToken.None);
        await RefreshTagsAsync();
        IsTagEventEditorOpen = false;
        IsEditingTagEvent = true;
        StatusMessage = $"Event '{SelectedPreset.Name}' saved.";
    }

    [RelayCommand]
    private void UseCurrentFrameForTagStart() => TagStartFrame = GetCurrentTaggingFrame();

    [RelayCommand]
    private void UseCurrentFrameForTagEnd() => TagEndFrame = GetCurrentTaggingFrame();

    public void SeekToTagEventStart(TagEventItemViewModel tagEvent)
    {
        SelectedTagEvent = tagEvent;
        CurrentFrame = Math.Max(0, tagEvent.StartFrame);
        StatusMessage = $"Jumped to event '{tagEvent.PresetName}'.";
    }

    [RelayCommand]
    private async Task DeleteSelectedTagAsync()
    {
        if (SelectedTagEvent is null)
        {
            return;
        }

        await _repository.DeleteTagEventAsync(_projectId, SelectedTagEvent.Id, CancellationToken.None);
        await RefreshTagsAsync();
        IsTagEventEditorOpen = false;
        IsEditingTagEvent = false;
        StatusMessage = "Event deleted.";
    }

    private static TeamSide NormalizeEventTeamSide(TeamSide teamSide)
    {
        return teamSide is TeamSide.Away ? TeamSide.Away : TeamSide.Home;
    }

    private async Task<IReadOnlyList<TagEvent>> NormalizeEventTeamSidesAsync(IReadOnlyList<TagEvent> events)
    {
        if (_projectId == Guid.Empty || events.Count == 0)
        {
            return events;
        }

        var normalizedEvents = new List<TagEvent>(events.Count);
        foreach (var sourceTagEvent in events)
        {
            var tagEvent = sourceTagEvent;
            var normalizedTeamSide = NormalizeEventTeamSide(tagEvent.TeamSide);
            if (normalizedTeamSide != tagEvent.TeamSide)
            {
                tagEvent = tagEvent with { TeamSide = normalizedTeamSide };
                await _repository.UpsertTagEventAsync(tagEvent, CancellationToken.None);
            }

            normalizedEvents.Add(tagEvent);
        }

        return normalizedEvents;
    }

    private async Task RefreshTagsAsync()
    {
        var query = new TagQuery(null, FilterPlayer, FilterPeriod, FilterText);
        var presetsById = TagPresets.ToDictionary((preset) => preset.Id);
        var events = await _repository.GetTagEventsAsync(_projectId, query, CancellationToken.None);
        events = await NormalizeEventTeamSidesAsync(events);
        var filtered = _tagService.Filter(events, query, presetsById);

        TagEvents.Clear();
        foreach (var tagEvent in filtered)
        {
            if (!presetsById.TryGetValue(tagEvent.TagPresetId, out var preset))
            {
                continue;
            }

            TagEvents.Add(new TagEventItemViewModel
            {
                Id = tagEvent.Id,
                TagPresetId = tagEvent.TagPresetId,
                PresetName = preset.Name,
                TeamSide = tagEvent.TeamSide.ToString(),
                StartFrame = tagEvent.StartFrame,
                EndFrame = tagEvent.EndFrame,
                StartTimeText = FormatTime(tagEvent.StartFrame, FramesPerSecond),
                EndTimeText = FormatTime(tagEvent.EndFrame, FramesPerSecond),
                Player = tagEvent.Player ?? string.Empty,
                Period = tagEvent.Period ?? string.Empty,
                Notes = tagEvent.Notes ?? string.Empty,
                IsSelectedForPlaylist = _selectedPlaylistTagEventIds.Contains(tagEvent.Id)
            });
        }

        var allEvents = await _repository.GetTagEventsAsync(_projectId, new TagQuery(null, null, null, null), CancellationToken.None);
        allEvents = await NormalizeEventTeamSidesAsync(allEvents);
        RefreshEventTypeItems(allEvents);
        RefreshStatistics(allEvents);
        RefreshTimeline(allEvents);

        ClipSummary = $"Segments: {_lastSegments.Count}";
        OnPropertyChanged(nameof(HasPlaylistSelection));
        OnPropertyChanged(nameof(CanCreatePlaylist));
        OnPropertyChanged(nameof(CanOpenAddToPlaylistDialog));
        OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
        OnPropertyChanged(nameof(SelectedPlaylistEventCount));
    }

    [RelayCommand]
    private void TogglePlaylistSelection(TagEventItemViewModel? tagEvent)
    {
        if (tagEvent is null)
        {
            return;
        }

        if (_selectedPlaylistTagEventIds.Contains(tagEvent.Id))
        {
            _selectedPlaylistTagEventIds.Remove(tagEvent.Id);
            tagEvent.IsSelectedForPlaylist = false;
        }
        else
        {
            _selectedPlaylistTagEventIds.Add(tagEvent.Id);
            tagEvent.IsSelectedForPlaylist = true;
        }

        StatusMessage = _selectedPlaylistTagEventIds.Count == 0
            ? "Подборка очищена."
            : $"Выбрано событий для подборки: {_selectedPlaylistTagEventIds.Count}.";
        OnPropertyChanged(nameof(HasPlaylistSelection));
        OnPropertyChanged(nameof(CanCreatePlaylist));
        OnPropertyChanged(nameof(CanOpenAddToPlaylistDialog));
        OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
        OnPropertyChanged(nameof(SelectedPlaylistEventCount));
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала откройте проект.";
            return;
        }

        var request = new CreatePlaylistRequestDto(
            _projectId,
            string.IsNullOrWhiteSpace(PlaylistName) ? $"Подборка {DateTime.Now:dd.MM HH:mm}" : PlaylistName.Trim(),
            _selectedPlaylistTagEventIds.ToList(),
            PreRollFrames,
            PostRollFrames,
            string.IsNullOrWhiteSpace(PlaylistDescription) ? null : PlaylistDescription.Trim(),
            GetProjectFrameLimit());

        try
        {
            var playlist = await _playlistService.CreatePlaylistAsync(request, CancellationToken.None);
            await RefreshPlaylistsAsync(CancellationToken.None);
            var repairedCount = ApplyLoadedPlaylist(playlist);
            StatusMessage = $"Плейлист '{playlist.Name}' создан.";
            if (repairedCount > 0)
            {
                StatusMessage += $" Восстановлены диапазоны для {repairedCount} клипов.";
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Create playlist failed");
            StatusMessage = $"Не удалось создать плейлист: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenAddToPlaylistDialog()
    {
        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала откройте проект.";
            return;
        }

        if (_selectedPlaylistTagEventIds.Count == 0)
        {
            StatusMessage = "Сначала выберите события для добавления в плейлист.";
            return;
        }

        if (Playlists.Count == 0)
        {
            StatusMessage = "Сначала создайте хотя бы один плейлист.";
            return;
        }

        SelectedTargetPlaylistForAdd = SelectedPlaylist ?? Playlists.FirstOrDefault();
        IsAddToPlaylistDialogOpen = true;
    }

    [RelayCommand]
    private void CloseAddToPlaylistDialog()
    {
        IsAddToPlaylistDialogOpen = false;
    }

    [RelayCommand]
    private async Task AddSelectedEventsToPlaylistAsync()
    {
        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала откройте проект.";
            return;
        }

        if (SelectedTargetPlaylistForAdd is null)
        {
            StatusMessage = "Выберите плейлист.";
            return;
        }

        if (_selectedPlaylistTagEventIds.Count == 0)
        {
            StatusMessage = "Сначала выберите события для добавления в плейлист.";
            return;
        }

        var request = new AddEventsToPlaylistRequestDto(
            _projectId,
            SelectedTargetPlaylistForAdd.Id,
            _selectedPlaylistTagEventIds.ToList(),
            GetProjectFrameLimit());

        try
        {
            var playlist = await _playlistService.AddEventsToPlaylistAsync(request, CancellationToken.None);
            await RefreshPlaylistsAsync(CancellationToken.None);
            ApplyLoadedPlaylist(playlist);
            _selectedPlaylistTagEventIds.Clear();
            foreach (var tagEvent in TagEvents)
            {
                tagEvent.IsSelectedForPlaylist = false;
            }

            IsAddToPlaylistDialogOpen = false;
            StatusMessage = $"События добавлены в плейлист '{playlist.Name}'.";
            OnPropertyChanged(nameof(HasPlaylistSelection));
            OnPropertyChanged(nameof(CanCreatePlaylist));
            OnPropertyChanged(nameof(CanOpenAddToPlaylistDialog));
            OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
            OnPropertyChanged(nameof(SelectedPlaylistEventCount));
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Add selected events to playlist failed");
            StatusMessage = $"Не удалось добавить события в плейлист: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenSelectedPlaylistAsync()
    {
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Выберите плейлист.";
            return;
        }

        var playlist = await _playlistService.GetPlaylistAsync(_projectId, SelectedPlaylist.Id, CancellationToken.None);
        if (playlist is null)
        {
            StatusMessage = "Плейлист не найден.";
            await RefreshPlaylistsAsync(CancellationToken.None);
            return;
        }

        var repairedCount = ApplyLoadedPlaylist(playlist);
        StatusMessage = $"Плейлист '{playlist.Name}' открыт.";
        if (repairedCount > 0)
        {
            StatusMessage += $" Восстановлены диапазоны для {repairedCount} клипов.";
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedPlaylistAsync()
    {
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Выберите плейлист.";
            return;
        }

        var playlistToDelete = SelectedPlaylist;
        await _playlistService.DeletePlaylistAsync(_projectId, playlistToDelete.Id, CancellationToken.None);

        if (_activePlaylistId == playlistToDelete.Id)
        {
            _activePlaylistId = Guid.Empty;
            _activePlaylistSegments = [];
            _lastSegments = [];
            _activePlaylistSegmentIndex = -1;
            IsPlaylistPlaybackActive = false;
            PlaylistItems.Clear();
            SelectedPlaylistItem = null;
            ClipSummary = "Segments: 0";
        IsTimelineFilterPopupOpen = false;
        HomeScore = 0;
        AwayScore = 0;
        OnPropertyChanged(nameof(HomeScore));
        OnPropertyChanged(nameof(AwayScore));
        OnPropertyChanged(nameof(HomeTeamDisplayName));
        OnPropertyChanged(nameof(AwayTeamDisplayName));
            OnPropertyChanged(nameof(CanPlayActivePlaylist));
        }

        await RefreshPlaylistsAsync(CancellationToken.None);
        StatusMessage = $"Плейлист '{playlistToDelete.Name}' удалён.";
    }

    [RelayCommand]
    private void SeekToPlaylistItem(PlaylistClipItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedPlaylistItem = item;
        CurrentFrame = Math.Max(0, item.ClipStartFrame);
        StatusMessage = $"Переход к клипу '{item.Label}'.";
    }

    [RelayCommand]
    private void PlayActivePlaylist()
    {
        if (_activePlaylistSegments.Count == 0)
        {
            StatusMessage = "Сначала откройте или создайте плейлист.";
            return;
        }

        _activePlaylistSegmentIndex = 0;
        StartPlaylistSegment(_activePlaylistSegmentIndex);
    }

    [RelayCommand]
    private void StopPlaylistPlayback()
    {
        if (!IsPlaylistPlaybackActive && _activePlaylistSegments.Count == 0)
        {
            return;
        }

        _mediaPlaybackService.Pause();
        IsPlaylistPlaybackActive = false;
        _activePlaylistSegmentIndex = -1;
        StatusMessage = "Воспроизведение плейлиста остановлено.";
    }

    [RelayCommand]
    private async Task AddAnnotationAsync()
    {
        var annotation = new Annotation(
            Guid.NewGuid(),
            _projectId,
            SelectedTagEvent?.Id,
            Math.Max(0, AnnotationStartFrame),
            Math.Max(AnnotationStartFrame, AnnotationEndFrame),
            SelectedShapeType,
            AnnotationX1,
            AnnotationY1,
            AnnotationX2,
            AnnotationY2,
            string.IsNullOrWhiteSpace(AnnotationText) ? null : AnnotationText,
            string.IsNullOrWhiteSpace(AnnotationColor) ? "#FFFFFF" : AnnotationColor,
            3);

        await _repository.UpsertAnnotationAsync(annotation, CancellationToken.None);
        await RefreshAnnotationsAsync();
    }

    [RelayCommand]
    private async Task BuildClipsAsync()
    {
        var events = await _repository.GetTagEventsAsync(_projectId, new TagQuery(null, FilterPlayer, FilterPeriod, FilterText), CancellationToken.None);
        var recipe = new ClipRecipe(
            Guid.NewGuid(),
            _projectId,
            SelectedPreset?.Name ?? "Clips",
            SelectedPreset?.Id,
            FilterPlayer,
            FilterPeriod,
            FilterText,
            PreRollFrames,
            PostRollFrames,
            DateTimeOffset.UtcNow);

        await _repository.UpsertClipRecipeAsync(recipe, CancellationToken.None);
        _lastSegments = _clipComposerService.BuildSegments(events, recipe, GetProjectFrameLimit());
        ClipSummary = $"Segments: {_lastSegments.Count}";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_lastSegments.Count == 0)
        {
            await BuildClipsAsync();
            if (_lastSegments.Count == 0)
            {
                StatusMessage = "No segments to export.";
                return;
            }
        }

        if (!HasReadableSourceVideo() && !CanUseBroadcastDvrAsSource())
        {
            StatusMessage = "Source video is missing.";
            return;
        }

        var annotationDtos = Annotations.Select((annotation) => new AnnotationDto(
            annotation.Id,
            annotation.StartFrame,
            annotation.EndFrame,
            annotation.ShapeType,
            annotation.X1,
            annotation.Y1,
            annotation.X2,
            annotation.Y2,
            annotation.Text,
            annotation.ColorHex,
            3)).ToList();

        var exportSegments = await EnrichSegmentsForExportAsync(_lastSegments);
        (string SourceVideoPath, IReadOnlyList<ClipSegmentDto> Segments) exportInput;
        try
        {
            exportInput = await PrepareExportInputAsync(
                exportSegments,
                Path.GetDirectoryName(ExportOutputPath) ?? GetResolvedExportFolderPath(),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            AppLogService.Warning(ex, "Prepare export input failed");
            StatusMessage = ex.Message;
            return;
        }

        var request = new ExportRequestDto(
            _projectId,
            exportInput.SourceVideoPath,
            exportInput.Segments,
            annotationDtos,
            ExportOutputPath,
            FramesPerSecond,
            ExportToCloud,
            ExportToCloud
                ? new YandexS3Options(YandexServiceUrl, YandexBucket, YandexAccessKey, YandexSecretKey, YandexRegion, YandexPrefix)
                : null);

        var result = await _exportService.ExportAsync(request, CancellationToken.None);
        if (!result.Success)
        {
            StatusMessage = $"Export error: {result.ErrorMessage}";
            return;
        }

        var job = new ExportJob(
            Guid.NewGuid(),
            _projectId,
            null,
            ExportToCloud ? ExportDestinationType.YandexObjectStorage : ExportDestinationType.Local,
            result.OutputPath,
            result.RemoteObjectKey,
            ExportJobStatus.Succeeded,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await _repository.UpsertExportJobAsync(job, CancellationToken.None);
        StatusMessage = result.RemoteUrl is null ? $"Exported to {result.OutputPath}" : $"Uploaded: {result.RemoteUrl}";
    }

    [RelayCommand]
    private async Task ExportFromDialogAsync()
    {
        if (IsExportInProgress)
        {
            return;
        }

        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала откройте проект.";
            return;
        }

        if (!HasReadableSourceVideo() && !CanUseBroadcastDvrAsSource())
        {
            StatusMessage = "Исходное видео не найдено.";
            return;
        }

        if (SelectedExportDestination == ExportDestinationOption.Telegram)
        {
            StatusMessage = "Экспорт в Telegram пока не реализован. Пока доступно сохранение в папку.";
            return;
        }

        IReadOnlyList<ClipSegmentDto> segments;
        try
        {
            segments = await ResolveExportSegmentsAsync();
        }
        catch (InvalidOperationException ex)
        {
            AppLogService.Warning(ex, "Resolve export segments failed");
            StatusMessage = ex.Message;
            return;
        }

        try
        {
            IsExportInProgress = true;
            ExportProgressText = "Подготавливаем экспорт...";
            StatusMessage = "Подготавливаем экспорт...";
            await Task.Yield();

            var exportFolder = GetResolvedExportFolderPath();
            Directory.CreateDirectory(exportFolder);
            ExportOutputPath = Path.Combine(exportFolder, BuildExportFileName() + GetExportFileExtension());
            var exportInput = await PrepareExportInputAsync(segments, exportFolder, CancellationToken.None);
            segments = exportInput.Segments;

            var annotationDtos = ExportIncludeTacticalDrawings
                ? Annotations.Select((annotation) => new AnnotationDto(
                    annotation.Id,
                    annotation.StartFrame,
                    annotation.EndFrame,
                    annotation.ShapeType,
                    annotation.X1,
                    annotation.Y1,
                    annotation.X2,
                    annotation.Y2,
                    annotation.Text,
                    annotation.ColorHex,
                    3)).ToList()
                : [];

            var request = new ExportRequestDto(
                _projectId,
                exportInput.SourceVideoPath,
                segments,
                annotationDtos,
                ExportOutputPath,
                FramesPerSecond,
                false,
                null);

            ExportProgressText = "Рендерим видео. Это может занять некоторое время...";
            StatusMessage = "Рендерим видео...";

            var result = await _exportService.ExportAsync(request, CancellationToken.None);
            if (!result.Success)
            {
                StatusMessage = $"Export error: {result.ErrorMessage}";
                return;
            }

            ExportProgressText = "Сохраняем результат...";

            var job = new ExportJob(
                Guid.NewGuid(),
                _projectId,
                _activePlaylistId == Guid.Empty ? null : _activePlaylistId,
                ExportDestinationType.Local,
                result.OutputPath,
                null,
                ExportJobStatus.Succeeded,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            await _repository.UpsertExportJobAsync(job, CancellationToken.None);

            IsExportDialogOpen = false;
            StatusMessage = SelectedExportDestination == ExportDestinationOption.Both
                ? $"Экспорт сохранён в папку. Отправка в Telegram пока не реализована: {result.OutputPath}"
                : $"Экспорт сохранён: {result.OutputPath}";
        }
        catch (InvalidOperationException ex)
        {
            AppLogService.Warning(ex, "Export from dialog failed");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsExportInProgress = false;
            ExportProgressText = "Подготовка к экспорту...";
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsStore.Save(new AppSettings
        {
            FfmpegPath = _settings.FfmpegPath,
            YandexServiceUrl = YandexServiceUrl,
            YandexBucket = YandexBucket,
            YandexAccessKey = YandexAccessKey,
            YandexSecretKey = YandexSecretKey,
            YandexRegion = YandexRegion,
            YandexPrefix = YandexPrefix
        });

        StatusMessage = "Настройки облака сохранены.";
    }

    private async Task RefreshRecentProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = await _repository.ListProjectsAsync(cancellationToken);
        var orderedProjects = projects
            .OrderByDescending((project) => project.UpdatedAtUtc)
            .ToList();

        var allProjectItems = new List<RecentProjectItemViewModel>(orderedProjects.Count);
        foreach (var project in orderedProjects)
        {
            var projectVideo = await _repository.GetProjectVideoAsync(project.Id, cancellationToken);
            allProjectItems.Add(CreateProjectItem(project, projectVideo));
        }

        ProjectPickerItems.Clear();
        foreach (var item in allProjectItems)
        {
            ProjectPickerItems.Add(item);
        }

        RecentProjects.Clear();
        foreach (var item in allProjectItems.Take(3))
        {
            RecentProjects.Add(item);
        }

        SelectedRecentProject = ProjectPickerItems.FirstOrDefault((item) => item.ProjectId == _projectId)
            ?? ProjectPickerItems.FirstOrDefault();
    }

    private static RecentProjectItemViewModel CreateProjectItem(Project project, ProjectVideo? projectVideo)
    {
        return new RecentProjectItemViewModel
        {
            ProjectId = project.Id,
            Name = project.Name,
            Matchup = FormatProjectMatchup(project),
            Summary = FormatProjectSummary(project, projectVideo),
            UpdatedAtText = $"Обновлен {project.UpdatedAtUtc.ToLocalTime():dd.MM.yyyy}"
        };
    }

    private void ResetCurrentProjectState()
    {
        _mediaPlaybackService.Close();
        _projectId = Guid.Empty;
        _projectFolderPath = string.Empty;
        ProjectName = "Hockey Analysis";
        HomeTeamDisplayName = "Команда хозяев";
        AwayTeamDisplayName = "Команда гостей";
        TagPresets.Clear();
        TagEvents.Clear();
        Annotations.Clear();
        Playlists.Clear();
        PlaylistItems.Clear();
        StatisticsItems.Clear();
        TimelineRows.Clear();
        TimelineTicks.Clear();
        TimelineFilters.Clear();
        _selectedPlaylistTagEventIds.Clear();
        _activePlaylistSegments = [];
        _activePlaylistSegmentIndex = -1;
        _activePlaylistId = Guid.Empty;
        _lastSegments = [];
        _currentBroadcastRecordingPath = null;
        _currentBroadcastRecordingStartFrame = 0;
        _broadcastRecordingStartFrame = 0;
        _broadcastDvrService.DetachSession();
        _broadcastTimelineStartedAtUtc = default;
        _broadcastTimelineOffsetFrame = 0;
        _lastBroadcastTimelineLayoutRefreshFrame = -1;
        SelectedPreset = null;
        SelectedTagEvent = null;
        SelectedPlaylist = null;
        SelectedPlaylistItem = null;
        SourceVideoPath = string.Empty;
        PlaybackVideoPath = string.Empty;
        IsLiveSource = false;
        IsBroadcastModeProject = false;
        IsBroadcastPanelHidden = true;
        IsBroadcastRecording = false;
        BroadcastRecordingPreviewSource = string.Empty;
        BroadcastDvrPreviewSource = string.Empty;
        IsBroadcastDvrRunning = false;
        BroadcastTimelineFrame = 0;
        CurrentFrame = 0;
        DurationFrames = 1;
        FramesPerSecond = 30;
        VideoWidth = 0;
        VideoHeight = 0;
        IsPlaying = false;
        IsPlaylistPlaybackActive = false;
        IsExportDialogOpen = false;
        ResetVideoZoomState();
        PlaylistName = "Новая подборка";
        PlaylistDescription = string.Empty;
        ClipSummary = "Segments: 0";
        ExportFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Video Analytics", "Exports");
        HomeScore = 0;
        AwayScore = 0;
        OnPropertyChanged(nameof(HomeScore));
        OnPropertyChanged(nameof(AwayScore));
        OnPropertyChanged(nameof(HasPlaylistSelection));
        OnPropertyChanged(nameof(CanCreatePlaylist));
        OnPropertyChanged(nameof(CanPlayActivePlaylist));
        OnPropertyChanged(nameof(SelectedPlaylistEventCount));
        OnPropertyChanged(nameof(CanCloseStartupScreen));
    }

    private async Task ResetProjectRuntimeAsync(CancellationToken cancellationToken)
    {
        if (IsBroadcastDvrRunning || _broadcastDvrService.IsRunning)
        {
            await StopBroadcastDvrAsync(cancellationToken);
        }
        else
        {
            StopBroadcastTimeline();
        }

        _mediaPlaybackService.Close();
        _broadcastDvrService.DetachSession();
        _currentBroadcastRecordingPath = null;
        _currentBroadcastRecordingStartFrame = 0;
        _broadcastRecordingStartFrame = 0;
        _broadcastTimelineStartedAtUtc = default;
        _broadcastTimelineOffsetFrame = 0;
        _lastBroadcastTimelineLayoutRefreshFrame = -1;
        IsBroadcastRecording = false;
        IsBroadcastRecordingOperationInProgress = false;
        BroadcastRecordingPreviewSource = string.Empty;
        BroadcastDvrPreviewSource = string.Empty;
        IsBroadcastDvrRunning = false;
        BroadcastTimelineFrame = 0;
        IsLiveSource = false;
        SourceVideoPath = string.Empty;
        PlaybackVideoPath = string.Empty;
        CurrentFrame = 0;
        DurationFrames = 1;
        FramesPerSecond = 30;
        VideoWidth = 0;
        VideoHeight = 0;
        IsPlaying = false;
        ResetVideoZoomState();
        RefreshPlaybackUiState();
    }

    private static string FormatProjectMatchup(Project project)
    {
        var hasHome = !string.IsNullOrWhiteSpace(project.HomeTeamName);
        var hasAway = !string.IsNullOrWhiteSpace(project.AwayTeamName);

        if (hasHome && hasAway)
        {
            return $"{project.HomeTeamName} - {project.AwayTeamName}";
        }

        if (hasHome)
        {
            return $"{project.HomeTeamName} - TBD";
        }

        if (hasAway)
        {
            return $"TBD - {project.AwayTeamName}";
        }

        return "Команды еще не указаны";
    }

    private static string FormatProjectSummary(Project project, ProjectVideo? projectVideo)
    {
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            return project.Description!;
        }

        if (project.IsBroadcastMode)
        {
            return "Режим трансляции";
        }

        if (projectVideo is not null)
        {
            return $"Видео: {projectVideo.Title}";
        }

        return "Проект готов к разбору.";
    }

    private async Task LoadProjectAsync(Project project, CancellationToken cancellationToken)
    {
        await ResetProjectRuntimeAsync(cancellationToken);

        _selectedPlaylistTagEventIds.Clear();
        _activePlaylistSegments = [];
        _activePlaylistSegmentIndex = -1;
        _activePlaylistId = Guid.Empty;
        _lastSegments = [];
        IsPlaylistPlaybackActive = false;
        IsExportDialogOpen = false;
        Playlists.Clear();
        PlaylistItems.Clear();
        StatisticsItems.Clear();
        SelectedPlaylist = null;
        SelectedPlaylistItem = null;
        ClipSummary = "Segments: 0";
        HomeScore = 0;
        AwayScore = 0;
        OnPropertyChanged(nameof(HomeScore));
        OnPropertyChanged(nameof(AwayScore));
        _projectId = project.Id;
        _projectFolderPath = project.ProjectFolderPath;
        ProjectName = project.Name;
        IsBroadcastModeProject = project.IsBroadcastMode;
        HomeTeamDisplayName = string.IsNullOrWhiteSpace(project.HomeTeamName) ? "Команда хозяев" : project.HomeTeamName;
        AwayTeamDisplayName = string.IsNullOrWhiteSpace(project.AwayTeamName) ? "Команда гостей" : project.AwayTeamName;
        OnPropertyChanged(nameof(HomeTeamDisplayName));
        OnPropertyChanged(nameof(AwayTeamDisplayName));
        PlaylistName = $"{project.Name} playlist";
        PlaylistDescription = string.Empty;
        ExportFolderPath = Path.Combine(_projectFolderPath, "exports");
        UpdateExportOutputPath();
        OnPropertyChanged(nameof(HasPlaylistSelection));
        OnPropertyChanged(nameof(CanCreatePlaylist));
        OnPropertyChanged(nameof(CanOpenAddToPlaylistDialog));
        OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
        OnPropertyChanged(nameof(SelectedPlaylistEventCount));
        OnPropertyChanged(nameof(CanPlayActivePlaylist));
        OnPropertyChanged(nameof(CanCloseStartupScreen));

        await EnsureDefaultPresetsAsync(cancellationToken);
        await LoadProjectVideoAsync(cancellationToken);
        await RefreshTagsAsync();
        await RefreshAnnotationsAsync();
        await RefreshPlaylistsAsync(cancellationToken);
    }

    private async Task EnsureDefaultPresetsAsync(CancellationToken cancellationToken)
    {
        var presets = await _repository.GetTagPresetsAsync(_projectId, cancellationToken);
        if (presets.Count == 0)
        {
            foreach (var preset in HockeyTagPresets.CreateDefaults(_projectId))
            {
                await _repository.UpsertTagPresetAsync(preset, cancellationToken);
            }

            presets = await _repository.GetTagPresetsAsync(_projectId, cancellationToken);
        }

        TagPresets.Clear();
        foreach (var preset in presets)
        {
            TagPresets.Add(preset);
        }

        RefreshEventTypeItems();
        SelectedPreset = TagPresets.FirstOrDefault();
    }

    private void RefreshEventTypeItems(IReadOnlyList<TagEvent>? allEvents = null)
    {
        var countsByPresetId = (allEvents ?? [])
            .GroupBy((tagEvent) => tagEvent.TagPresetId)
            .ToDictionary((group) => group.Key, (group) => group.Count());
        var selectedPresetId = SelectedPreset?.Id ?? SelectedEventTypeItem?.Id;

        EventTypeItems.Clear();
        foreach (var preset in TagPresets)
        {
            EventTypeItems.Add(new EventTypeItemViewModel
            {
                Preset = preset,
                EventCount = countsByPresetId.TryGetValue(preset.Id, out var count) ? count : 0
            });
        }

        if (selectedPresetId is not null)
        {
            var selectedItem = EventTypeItems.FirstOrDefault((item) => item.Id == selectedPresetId.Value);
            if (selectedItem is not null)
            {
                SelectedEventTypeItem = selectedItem;
                SelectedPreset = selectedItem.Preset;
            }
        }
    }

    private void RefreshStatistics(IReadOnlyList<TagEvent> allEvents)
    {
        var events = allEvents ?? [];
        var countsByPresetId = events
            .GroupBy((tagEvent) => tagEvent.TagPresetId)
            .ToDictionary(
                (group) => group.Key,
                (group) => new
                {
                    Home = group.Count((tagEvent) => NormalizeEventTeamSide(tagEvent.TeamSide) == TeamSide.Home),
                    Away = group.Count((tagEvent) => NormalizeEventTeamSide(tagEvent.TeamSide) == TeamSide.Away)
                });

        StatisticsItems.Clear();
        foreach (var preset in TagPresets.Where((preset) => preset.ShowInStatistics))
        {
            var counts = countsByPresetId.TryGetValue(preset.Id, out var value) ? value : new { Home = 0, Away = 0 };
            StatisticsItems.Add(new StatisticsBarItemViewModel
            {
                Name = preset.Name,
                HomeCount = counts.Home,
                AwayCount = counts.Away
            });
        }

        var goalPreset = TagPresets.FirstOrDefault((preset) =>
                string.Equals(preset.Hotkey?.Trim(), "G", StringComparison.OrdinalIgnoreCase))
            ?? TagPresets.FirstOrDefault((preset) =>
                string.Equals(preset.Name, "Goal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preset.Name, "???", StringComparison.OrdinalIgnoreCase));

        if (goalPreset is not null && countsByPresetId.TryGetValue(goalPreset.Id, out var goalCounts))
        {
            HomeScore = goalCounts.Home;
            AwayScore = goalCounts.Away;
        }
        else
        {
            HomeScore = 0;
            AwayScore = 0;
        }

        OnPropertyChanged(nameof(HomeScore));
        OnPropertyChanged(nameof(AwayScore));
    }


    private void RefreshTimeline(IReadOnlyList<TagEvent> allEvents)
    {
        _allTimelineEvents = allEvents
            .OrderBy((tagEvent) => tagEvent.StartFrame)
            .ToArray();

        RefreshTimelineFilters();
        RefreshTimelineTicks();
        RefreshTimelineRows();
    }

    private void RefreshTimelineFilters()
    {
        var visibilityByPresetId = TimelineFilters.ToDictionary((item) => item.PresetId, (item) => item.IsVisible);

        foreach (var item in TimelineFilters)
        {
            item.PropertyChanged -= OnTimelineFilterItemPropertyChanged;
        }

        TimelineFilters.Clear();
        foreach (var preset in TagPresets)
        {
            var filterItem = new TimelineFilterItemViewModel
            {
                PresetId = preset.Id,
                Name = preset.Name,
                ColorHex = preset.ColorHex,
                IsVisible = !visibilityByPresetId.TryGetValue(preset.Id, out var isVisible) || isVisible
            };
            filterItem.PropertyChanged += OnTimelineFilterItemPropertyChanged;
            TimelineFilters.Add(filterItem);
        }

        OnPropertyChanged(nameof(VisibleTimelineFilterCount));
        OnPropertyChanged(nameof(TimelineFilterButtonText));
    }

    private void OnTimelineFilterItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(TimelineFilterItemViewModel.IsVisible), StringComparison.Ordinal))
        {
            return;
        }

        OnPropertyChanged(nameof(VisibleTimelineFilterCount));
        OnPropertyChanged(nameof(TimelineFilterButtonText));
        RefreshTimelineRows();
    }

    private void RefreshTimelineTicks()
    {
        TimelineTicks.Clear();

        var totalSeconds = Math.Max(1d, GetDurationSeconds());
        var secondsPerTick = GetTimelineTickIntervalSeconds(totalSeconds);
        var (startSeconds, endSeconds) = GetVisibleTimelineSeconds(totalSeconds, secondsPerTick);
        var startIndex = Math.Max(0, (int)Math.Floor(startSeconds / secondsPerTick));
        var endIndex = Math.Max(startIndex, (int)Math.Ceiling(endSeconds / secondsPerTick));

        for (var index = startIndex; index <= endIndex; index++)
        {
            var seconds = Math.Min(totalSeconds, index * secondsPerTick);
            var frame = (long)Math.Round(seconds * Math.Max(1d, FramesPerSecond));
            TimelineTicks.Add(new TimelineTickItemViewModel
            {
                Label = FormatTimelineTick(seconds),
                Left = CalculateTimelineX(frame)
            });
        }
    }

    private (double StartSeconds, double EndSeconds) GetVisibleTimelineSeconds(double totalSeconds, double secondsPerTick)
    {
        if (_timelineViewportWidth <= 0d || TimelineCanvasWidth <= _timelineViewportWidth)
        {
            return (0d, totalSeconds);
        }

        var viewportStart = Math.Clamp(_timelineViewportOffsetX / TimelineCanvasWidth * totalSeconds, 0d, totalSeconds);
        var viewportEnd = Math.Clamp((_timelineViewportOffsetX + _timelineViewportWidth) / TimelineCanvasWidth * totalSeconds, 0d, totalSeconds);
        var padding = Math.Max(secondsPerTick * 4d, (viewportEnd - viewportStart) * 0.25d);

        return (Math.Max(0d, viewportStart - padding), Math.Min(totalSeconds, viewportEnd + padding));
    }

    private void RefreshTimelineRows()
    {
        var visiblePresetIds = TimelineFilters
            .Where((item) => item.IsVisible)
            .Select((item) => item.PresetId)
            .ToHashSet();

        TimelineRows.Clear();
        foreach (var preset in TagPresets.Where((item) => visiblePresetIds.Contains(item.Id)))
        {
            var row = new TimelineRowItemViewModel
            {
                PresetId = preset.Id,
                Name = preset.Name,
                ColorHex = preset.ColorHex
            };

            foreach (var tagEvent in _allTimelineEvents.Where((item) => item.TagPresetId == preset.Id))
            {
                var left = CalculateTimelineX(tagEvent.StartFrame);
                var endLeft = CalculateTimelineX(Math.Max(tagEvent.StartFrame, tagEvent.EndFrame));
                var width = tagEvent.EndFrame > tagEvent.StartFrame
                    ? Math.Max(6d, endLeft - left)
                    : TimelineInstantWidth;

                row.Segments.Add(new TimelineEventSegmentItemViewModel
                {
                    StartFrame = Math.Max(0, tagEvent.StartFrame),
                    Left = left,
                    Width = width,
                    ColorHex = preset.ColorHex
                });
            }

            TimelineRows.Add(row);
        }
    }

    private double GetDurationSeconds()
    {
        return TimelineDurationFrames <= 0 || FramesPerSecond <= 0
            ? 300d
            : TimelineDurationFrames / FramesPerSecond;
    }

    private double GetTimelineTickIntervalSeconds(double totalSeconds)
    {
        var pixelsPerSecond = TimelineCanvasWidth / Math.Max(1d, totalSeconds);
        double[] intervals = [0.1d, 0.2d, 0.5d, 1d, 2d, 5d, 10d, 15d, 30d, 60d, 120d, 300d, 600d, 900d, 1800d, 3600d];
        var interval = intervals.FirstOrDefault((candidate) => candidate * pixelsPerSecond >= TimelineTickMinimumSpacing);

        return interval > 0d ? interval : intervals[^1];
    }

    private static string FormatTimelineTick(double seconds)
    {
        var roundedTenths = (int)Math.Round(seconds * 10d);
        var time = TimeSpan.FromSeconds(roundedTenths / 10d);
        var tenths = roundedTenths % 10;

        if (tenths != 0)
        {
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{tenths}";
        }

        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private double GetTimelineMinimumZoom()
    {
        var durationWidth = GetDurationSeconds() * TimelineSecondWidth;
        if (_timelineViewportWidth <= 0d || durationWidth <= 0d)
        {
            return TimelineFallbackMinimumZoom;
        }

        return Math.Min(1d, Math.Max(TimelineFallbackMinimumZoom, _timelineViewportWidth / durationWidth));
    }

    private double CalculateTimelineX(long frame)
    {
        if (TimelineDurationFrames <= 0)
        {
            return 0d;
        }

        var normalized = Math.Clamp(frame / (double)Math.Max(1L, TimelineDurationFrames), 0d, 1d);
        return normalized * TimelineCanvasWidth;
    }

    private async Task LoadProjectVideoAsync(CancellationToken cancellationToken)
    {
        var projectVideo = await _repository.GetProjectVideoAsync(_projectId, cancellationToken);
        if (projectVideo is null)
        {
            _mediaPlaybackService.Close();
            SourceVideoPath = string.Empty;
            PlaybackVideoPath = string.Empty;
            IsLiveSource = false;
            FramesPerSecond = 30;
            DurationFrames = 1;
            VideoWidth = 0;
            VideoHeight = 0;
            CurrentFrame = 0;
            ResetVideoZoomState();
            if (!IsBroadcastModeProject)
            {
                StopBroadcastTimeline();
            }

            return;
        }

        SourceVideoPath = projectVideo.StoredFilePath;
        IsLiveSource = false;
        try
        {
            var playbackVideoPath = await ResolvePlaybackVideoPathAsync(projectVideo, cancellationToken);
            PlaybackVideoPath = playbackVideoPath;

            var metadata = await _mediaPlaybackService.OpenAsync(playbackVideoPath, cancellationToken);
            FramesPerSecond = metadata.FramesPerSecond;
            DurationFrames = Math.Max(1, metadata.DurationFrames);
            VideoWidth = metadata.Width;
            VideoHeight = metadata.Height;
            CurrentFrame = 0;
            IsPlaying = false;
            ResetVideoZoomState();
            RefreshPlaybackUiState();
        }
        catch
        {
            AppLogService.Warning("Video file from project is missing or unavailable.", "Load project video");
            StatusMessage = "Video file from project is missing or unavailable.";
        }
    }

    private async Task<string> ResolvePlaybackVideoPathAsync(ProjectVideo projectVideo, CancellationToken cancellationToken)
    {
        if (!File.Exists(projectVideo.StoredFilePath))
        {
            return projectVideo.StoredFilePath;
        }

        try
        {
            StatusMessage = "Создаем proxy-видео для плавной перемотки...";
            var result = await _videoProxyService.EnsureProxyAsync(
                projectVideo.StoredFilePath,
                _projectFolderPath,
                cancellationToken);

            if (!PathsEqual(result.ProxyFilePath, projectVideo.StoredFilePath) &&
                !string.Equals(result.ProxyFilePath, projectVideo.ProxyFilePath, StringComparison.OrdinalIgnoreCase))
            {
                await _repository.UpsertProjectVideoAsync(projectVideo with { ProxyFilePath = result.ProxyFilePath }, cancellationToken);
            }

            StatusMessage = result.Created
                ? "Proxy-видео готово. Плеер использует оптимизированную копию."
                : "Плеер использует proxy-видео.";

            return result.ProxyFilePath;
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Create proxy video failed");
            StatusMessage = $"Proxy-видео не создано, используется оригинал: {ex.Message}";
            return projectVideo.StoredFilePath;
        }
    }

    private async Task RefreshAnnotationsAsync()
    {
        var frameLimit = GetProjectFrameLimit();
        var annotations = await _repository.GetAnnotationsAsync(_projectId, new FrameRange(0, frameLimit <= 0 ? long.MaxValue : frameLimit), CancellationToken.None);
        Annotations.Clear();
        foreach (var annotation in annotations)
        {
            Annotations.Add(new AnnotationItemViewModel
            {
                Id = annotation.Id,
                ShapeType = annotation.ShapeType,
                StartFrame = annotation.StartFrame,
                EndFrame = annotation.EndFrame,
                X1 = annotation.X1,
                Y1 = annotation.Y1,
                X2 = annotation.X2,
                Y2 = annotation.Y2,
                ColorHex = annotation.ColorHex,
                Text = annotation.Text ?? string.Empty
            });
        }
    }

    private static string NormalizePlaylistDescription(string? description, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return allowEmpty ? string.Empty : "??? ????????";
        }

        if (LooksLikeMojibake(description))
        {
            return allowEmpty ? string.Empty : "??? ????????";
        }

        return description;
    }

    private static bool LooksLikeMojibake(string value)
    {
        return value.Contains("?'?", StringComparison.Ordinal)
            || value.Contains("??", StringComparison.Ordinal)
            || value.Contains("?", StringComparison.Ordinal)
            || value.Contains("?", StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshPlaylistsAsync(CancellationToken cancellationToken)
    {
        var playlists = await _playlistService.GetPlaylistsAsync(_projectId, cancellationToken);

        Playlists.Clear();
        foreach (var playlist in playlists)
        {
            Playlists.Add(new PlaylistSummaryItemViewModel
            {
                Id = playlist.Id,
                Name = playlist.Name,
                Description = string.IsNullOrWhiteSpace(playlist.Description) ? "Без описания" : playlist.Description,
                ItemCount = playlist.ItemCount,
                UpdatedAtText = $"{playlist.ItemCount} клипов • {playlist.UpdatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}"
            });
        }

        SelectedPlaylist = Playlists.FirstOrDefault((item) => item.Id == SelectedPlaylist?.Id) ?? Playlists.FirstOrDefault();
        SelectedTargetPlaylistForAdd = SelectedTargetPlaylistForAdd is null
            ? SelectedPlaylist
            : Playlists.FirstOrDefault((item) => item.Id == SelectedTargetPlaylistForAdd.Id) ?? SelectedPlaylist;
        OnPropertyChanged(nameof(CanOpenAddToPlaylistDialog));
        OnPropertyChanged(nameof(CanAddSelectedEventsToPlaylist));
    }

    private int ApplyLoadedPlaylist(PlaylistDetailsDto playlist)
    {
        _activePlaylistId = playlist.Id;
        var repairedCount = 0;
        _activePlaylistSegments = playlist.Items
            .OrderBy((item) => item.SortOrder)
            .Select((item) =>
            {
                var resolvedRange = ResolvePlaylistItemRange(item);
                if (resolvedRange.WasRepaired)
                {
                    repairedCount++;
                }

                return new ClipSegmentDto(item.TagEventId, resolvedRange.ClipStartFrame, resolvedRange.ClipEndFrame, item.Label, item.Player);
            })
            .ToList();

        _lastSegments = _activePlaylistSegments;
        _activePlaylistSegmentIndex = -1;
        IsPlaylistPlaybackActive = false;
        PlaylistName = playlist.Name;
        PlaylistDescription = NormalizePlaylistDescription(playlist.Description, allowEmpty: true);
        ClipSummary = $"Segments: {_lastSegments.Count}";

        PlaylistItems.Clear();
        foreach (var item in playlist.Items.OrderBy((playlistItem) => playlistItem.SortOrder))
        {
            var resolvedRange = ResolvePlaylistItemRange(item);
            PlaylistItems.Add(new PlaylistClipItemViewModel
            {
                Id = item.Id,
                TagEventId = item.TagEventId,
                Label = item.Label,
                Player = string.IsNullOrWhiteSpace(item.Player) ? "Без игрока" : item.Player,
                TeamSide = item.TeamSide.ToString(),
                ClipStartFrame = resolvedRange.ClipStartFrame,
                ClipEndFrame = resolvedRange.ClipEndFrame,
                FrameRangeText = $"{FormatTime(resolvedRange.ClipStartFrame, FramesPerSecond)} → {FormatTime(resolvedRange.ClipEndFrame, FramesPerSecond)}"
            });
        }

        SelectedPlaylist = Playlists.FirstOrDefault((candidate) => candidate.Id == playlist.Id) ?? SelectedPlaylist;
        SelectedTargetPlaylistForAdd = Playlists.FirstOrDefault((candidate) => candidate.Id == playlist.Id) ?? SelectedTargetPlaylistForAdd;
        SelectedPlaylistItem = PlaylistItems.FirstOrDefault();
        OnPropertyChanged(nameof(CanPlayActivePlaylist));
        return repairedCount;
    }

    private static (long ClipStartFrame, long ClipEndFrame, bool WasRepaired) ResolvePlaylistItemRange(PlaylistItemDto item)
    {
        var clipStartFrame = Math.Max(0, item.ClipStartFrame);
        var clipEndFrame = Math.Max(clipStartFrame, item.ClipEndFrame);

        if (clipEndFrame > clipStartFrame || item.EventEndFrame <= item.EventStartFrame)
        {
            return (clipStartFrame, clipEndFrame, false);
        }

        var repairedStartFrame = Math.Max(0, item.EventStartFrame - Math.Max(0, item.PreRollFrames));
        var repairedEndFrame = Math.Max(repairedStartFrame, item.EventEndFrame + Math.Max(0, item.PostRollFrames));
        return repairedEndFrame > repairedStartFrame
            ? (repairedStartFrame, repairedEndFrame, true)
            : (clipStartFrame, clipEndFrame, false);
    }

    private void OpenExportDialogCore(ExportSourceOption defaultSource)
    {
        if (_projectId == Guid.Empty)
        {
            StatusMessage = "Сначала откройте проект.";
            return;
        }

        SelectedExportSource = defaultSource == ExportSourceOption.Playlist && _activePlaylistSegments.Count == 0
            ? ExportSourceOption.AllClips
            : defaultSource;
        ExportFolderPath = GetResolvedExportFolderPath();
        UpdateExportOutputPath();
        IsExportDialogOpen = true;
    }

    private async Task<IReadOnlyList<ClipSegmentDto>> ResolveExportSegmentsAsync()
    {
        IReadOnlyList<ClipSegmentDto> segments;
        switch (SelectedExportSource)
        {
            case ExportSourceOption.Playlist:
                if (_activePlaylistSegments.Count == 0)
                {
                    throw new InvalidOperationException("Откройте плейлист перед экспортом.");
                }

                segments = _activePlaylistSegments;
                break;

            case ExportSourceOption.FullMatch:
                var frameLimit = GetProjectFrameLimit();
                if (frameLimit <= 1)
                {
                    throw new InvalidOperationException("Для проекта еще не загружено видео.");
                }

                segments =
                [
                    new ClipSegmentDto(Guid.Empty, 0, Math.Max(0, frameLimit - 1), ProjectName, null)
                ];
                break;

            default:
                await BuildClipsAsync();
                if (_lastSegments.Count == 0)
                {
                    throw new InvalidOperationException("Нет клипов для экспорта по текущим фильтрам.");
                }

                segments = _lastSegments;
                break;
        }

        return await EnrichSegmentsForExportAsync(segments);
    }

    private string GetResolvedExportFolderPath()
    {
        if (!string.IsNullOrWhiteSpace(ExportFolderPath))
        {
            return ExportFolderPath.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_projectFolderPath))
        {
            return Path.Combine(_projectFolderPath, "exports");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Video Analytics", "Exports");
    }

    private bool HasReadableSourceVideo()
    {
        return !string.IsNullOrWhiteSpace(SourceVideoPath) && File.Exists(SourceVideoPath);
    }

    private bool CanUseBroadcastDvrAsSource()
    {
        if (!IsBroadcastModeProject)
        {
            return false;
        }

        if ((IsBroadcastDvrRunning && _broadcastDvrService.IsRunning)
            || _broadcastDvrService.HasExportableArchive)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(_projectFolderPath)
            && _broadcastDvrService.TryAttachLatestSession(_projectFolderPath);
    }

    private long GetProjectFrameLimit()
    {
        if (IsBroadcastTimelineActive)
        {
            UpdateBroadcastTimeline();
            return Math.Max(Math.Max(1, TimelineDurationFrames), GetBroadcastDvrFrameLimit());
        }

        if (IsBroadcastModeProject)
        {
            return GetBroadcastDvrFrameLimit();
        }

        return Math.Max(1, DurationFrames);
    }

    private long GetBroadcastDvrFrameLimit()
    {
        return CanUseBroadcastDvrAsSource()
            ? Math.Max(1, _broadcastDvrService.GetAvailableFrameLimit())
            : Math.Max(1, DurationFrames);
    }

    private async Task<(string SourceVideoPath, IReadOnlyList<ClipSegmentDto> Segments)> PrepareExportInputAsync(
        IReadOnlyList<ClipSegmentDto> segments,
        string exportFolderPath,
        CancellationToken cancellationToken)
    {
        if (CanUseBroadcastDvrAsSource())
        {
            ExportProgressText = "Собираем источник из live-DVR...";
            StatusMessage = "Собираем источник из live-DVR...";
            var dvrSourceFolderPath = Path.Combine(exportFolderPath, "_live-dvr-sources");
            var prepared = await _broadcastDvrService.PrepareExportSourceAsync(
                segments,
                FramesPerSecond,
                dvrSourceFolderPath,
                cancellationToken);

            return (prepared.SourceVideoPath, prepared.Segments);
        }

        if (HasReadableSourceVideo())
        {
            return (SourceVideoPath, segments);
        }

        throw new InvalidOperationException("Исходное видео не найдено, а live-DVR еще не запущен.");
    }

    private async Task<IReadOnlyList<ClipSegmentDto>> EnrichSegmentsForExportAsync(IReadOnlyList<ClipSegmentDto> segments)
    {
        if (_projectId == Guid.Empty || segments.Count == 0)
        {
            return segments;
        }

        var allTagEvents = await _repository.GetTagEventsAsync(
            _projectId,
            new TagQuery(null, null, null, null, null, null),
            CancellationToken.None);

        var tagEventsById = allTagEvents.ToDictionary((tagEvent) => tagEvent.Id);
        var presetsById = TagPresets.ToDictionary((preset) => preset.Id);
        var totalSegments = segments.Count;

        return segments
            .Select((segment, index) =>
            {
                if (!tagEventsById.TryGetValue(segment.TagEventId, out var tagEvent))
                {
                    return segment;
                }

                presetsById.TryGetValue(tagEvent.TagPresetId, out var preset);
                var counterText = $"{index + 1}/{totalSegments}";
                var label = string.IsNullOrWhiteSpace(segment.Label)
                    ? preset?.Name ?? "Событие"
                    : segment.Label;

                return segment with
                {
                    Label = label,
                    Player = string.IsNullOrWhiteSpace(segment.Player) ? tagEvent.Player : segment.Player,
                    TeamSide = tagEvent.TeamSide,
                    TeamName = ResolveExportTeamName(tagEvent.TeamSide),
                    Period = string.IsNullOrWhiteSpace(tagEvent.Period) ? null : tagEvent.Period.Trim(),
                    MatchClockText = FormatTime(tagEvent.StartFrame, FramesPerSecond),
                    AccentColorHex = string.IsNullOrWhiteSpace(segment.AccentColorHex) ? preset?.ColorHex : segment.AccentColorHex,
                    CounterText = counterText
                };
            })
            .ToList();
    }

    private string? ResolveExportTeamName(TeamSide teamSide)
    {
        return teamSide switch
        {
            TeamSide.Home => string.IsNullOrWhiteSpace(HomeTeamDisplayName) ? "Хозяева" : HomeTeamDisplayName,
            TeamSide.Away => string.IsNullOrWhiteSpace(AwayTeamDisplayName) ? "Гости" : AwayTeamDisplayName,
            TeamSide.Neutral => "Нейтральное событие",
            _ => null
        };
    }

    private void UpdateExportOutputPath()
    {
        var folderPath = GetResolvedExportFolderPath();
        ExportOutputPath = Path.Combine(folderPath, BuildExportFileName() + GetExportFileExtension());
    }

    private string BuildExportFileName()
    {
        var rawName = SelectedExportSource switch
        {
            ExportSourceOption.Playlist when !string.IsNullOrWhiteSpace(PlaylistName) => PlaylistName.Trim(),
            ExportSourceOption.Playlist when SelectedPlaylist is not null => SelectedPlaylist.Name,
            ExportSourceOption.FullMatch => $"{ProjectName} full match",
            _ when SelectedPreset is not null => $"{ProjectName} {SelectedPreset.Name}",
            _ => $"{ProjectName} clips"
        };

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(rawName.Select((character) => invalidChars.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "video-analysis-export" : sanitized.Trim();
    }

    private string GetExportFileExtension()
    {
        return SelectedExportFormat switch
        {
            ExportFormatOption.Avi => ".avi",
            ExportFormatOption.Mov => ".mov",
            _ => ".mp4"
        };
    }

    private void StartPlaylistSegment(int index)
    {
        if (index < 0 || index >= _activePlaylistSegments.Count)
        {
            StopPlaylistPlayback();
            return;
        }

        var segment = _activePlaylistSegments[index];
        _activePlaylistSegmentIndex = index;
        SelectedPlaylistItem = index < PlaylistItems.Count ? PlaylistItems[index] : null;
        _mediaPlaybackService.SeekToFrame(segment.StartFrame);
        _mediaPlaybackService.Play();
        IsPlaylistPlaybackActive = true;
        StatusMessage = $"Плейлист: клип {index + 1}/{_activePlaylistSegments.Count} '{segment.Label}'.";
    }

    private void AdvancePlaylistPlayback(long currentFrame)
    {
        if (!IsPlaylistPlaybackActive || _activePlaylistSegmentIndex < 0 || _activePlaylistSegmentIndex >= _activePlaylistSegments.Count)
        {
            return;
        }

        var currentSegment = _activePlaylistSegments[_activePlaylistSegmentIndex];
        if (currentFrame <= currentSegment.EndFrame)
        {
            return;
        }

        var nextIndex = _activePlaylistSegmentIndex + 1;
        if (nextIndex >= _activePlaylistSegments.Count)
        {
            _mediaPlaybackService.Pause();
            IsPlaylistPlaybackActive = false;
            _activePlaylistSegmentIndex = -1;
            StatusMessage = "Плейлист воспроизведен полностью.";
            return;
        }

        StartPlaylistSegment(nextIndex);
    }

    private void OnPlaybackFrameChanged(object? sender, long frame)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ignoreFrameChange = true;
            DurationFrames = Math.Max(1, _mediaPlaybackService.DurationFrames);
            CurrentFrame = frame;
            _ignoreFrameChange = false;
            AdvancePlaylistPlayback(frame);
        }, DispatcherPriority.Background);
    }

    public void ForceAttachVideoHandle(IntPtr nativeHandle)
    {
        if (_mediaPlaybackService is LibVlcMediaPlaybackService service)
        {
            service.SetVideoOutputHandle(nativeHandle);
        }
    }

#if WINDOWS_MPV
    public void AttachMpvContext(MpvContext mpvContext)
    {
        if (_mediaPlaybackService is MpvMediaPlaybackService service)
        {
            service.AttachMpvContext(mpvContext);
        }
    }
#endif

    public void RefreshPlaybackUiState()
    {
        Volume = _mediaPlaybackService.Volume;
        IsMuted = _mediaPlaybackService.IsMuted;
        PlaybackRate = _mediaPlaybackService.PlaybackRate <= 0 ? 1.0 : _mediaPlaybackService.PlaybackRate;
        VideoWidth = _mediaPlaybackService.VideoWidth;
        VideoHeight = _mediaPlaybackService.VideoHeight;
        IsPlaying = _mediaPlaybackService.IsPlaying;
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(DurationTimeText));
    }

    public void ShutdownBroadcastRecording()
    {
        IsBroadcastRecording = false;
        IsBroadcastRecordingOperationInProgress = false;
        BroadcastRecordingPreviewSource = string.Empty;
        BroadcastDvrPreviewSource = string.Empty;
        IsBroadcastDvrRunning = false;
        _broadcastDvrService.ShutdownFast();
    }

    public void OpenPresetEditor(TagPreset preset)
    {
        SelectedPreset = preset;
        IsEditingPreset = true;
        ResetPresetEditorFields();
        IsPresetEditorOpen = true;
    }

    public void OpenTagEventEditor(TagEventItemViewModel tagEvent)
    {
        SelectedTagEvent = tagEvent;
        SelectedPreset = TagPresets.FirstOrDefault((preset) => preset.Id == tagEvent.TagPresetId) ?? SelectedPreset;
        TagStartFrame = tagEvent.StartFrame;
        TagEndFrame = tagEvent.EndFrame;
        TagPlayer = tagEvent.Player;
        TagPeriod = tagEvent.Period;
        TagNotes = tagEvent.Notes;
        TagTeamSide = Enum.TryParse<TeamSide>(tagEvent.TeamSide, out var parsedTeamSide)
            ? NormalizeEventTeamSide(parsedTeamSide)
            : TeamSide.Home;
        IsEditingTagEvent = true;
        IsTagEventEditorOpen = true;
    }

    private static string FormatTime(long frame, double framesPerSecond)
    {
        var fps = framesPerSecond <= 0 ? 30d : framesPerSecond;
        var totalSeconds = Math.Max(0, (int)Math.Floor(frame / fps));
        var time = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private bool HasHotkeyConflict(string candidateHotkey)
    {
        if (string.IsNullOrEmpty(candidateHotkey))
        {
            return false;
        }

        var editedPresetId = SelectedPreset?.Id;
        return TagPresets.Any((preset) =>
            preset.Id != editedPresetId &&
            string.Equals(preset.Hotkey?.Trim(), candidateHotkey, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeSingleEnglishHotkey(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        for (var index = value.Length - 1; index >= 0; index--)
        {
            var character = value[index];
            if (character is >= 'A' and <= 'Z')
            {
                return character.ToString();
            }

            if (character is >= 'a' and <= 'z')
            {
                return char.ToUpperInvariant(character).ToString();
            }
        }

        return null;
    }
}

public enum ExportSourceOption
{
    AllClips,
    Playlist,
    FullMatch
}

public enum ExportFormatOption
{
    Mp4,
    Avi,
    Mov
}

public enum ExportQualityOption
{
    Low720p,
    Medium1080p,
    High4K
}

public enum ExportDestinationOption
{
    Folder,
    Telegram,
    Both
}





























