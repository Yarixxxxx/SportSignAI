using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Avalonia;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using VideoAnalysis.App.Media;
using VideoAnalysis.App.Services;
using VideoAnalysis.App.ViewModels.Items;
using VideoAnalysis.App.ViewModels.Shell;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Models;
using VideoAnalysis.Infrastructure.Media;
#if WINDOWS_MPV
using HanumanInstitute.LibMpv.Avalonia;
using MpvContext = HanumanInstitute.LibMpv.MpvContext;
#endif

namespace VideoAnalysis.App.Views;

public partial class MainWindow : Window
{
    private const string PanelPlayer = "Player";
    private const string PanelTimeline = "Timeline";
    private const string PanelEvents = "Events";
    private const string PanelAnalysis = "Analysis";
    private const string PanelBroadcast = "Broadcast";
    private const double TimelineSeekHandleHitWidth = 16d;
    private const double WorkspacePanelDragThreshold = 6d;
    private const int GwlWndProc = -4;
    private const int VideoWheelDuplicateWindowMs = 35;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmGesture = 0x0119;
    private const uint GidZoom = 3;
    private const uint GfBegin = 0x00000001;
    private const uint GfEnd = 0x00000004;
    private const uint GcZoom = 0x00000001;

    private ToggleButton FileMenuButton => this.FindControl<ToggleButton>(nameof(FileMenuButton))
        ?? throw new InvalidOperationException("FileMenuButton was not found.");
    private ToggleButton MarkupMenuButton => this.FindControl<ToggleButton>(nameof(MarkupMenuButton))
        ?? throw new InvalidOperationException("MarkupMenuButton was not found.");
    private ToggleButton ViewMenuButton => this.FindControl<ToggleButton>(nameof(ViewMenuButton))
        ?? throw new InvalidOperationException("ViewMenuButton was not found.");
    private ToggleButton SettingsMenuButton => this.FindControl<ToggleButton>(nameof(SettingsMenuButton))
        ?? throw new InvalidOperationException("SettingsMenuButton was not found.");
    private ToggleButton HelpMenuButton => this.FindControl<ToggleButton>(nameof(HelpMenuButton))
        ?? throw new InvalidOperationException("HelpMenuButton was not found.");
    private Border TopMenuBar => this.FindControl<Border>(nameof(TopMenuBar))
        ?? throw new InvalidOperationException("TopMenuBar was not found.");
    private Border PlayerPanel => this.FindControl<Border>(nameof(PlayerPanel))
        ?? throw new InvalidOperationException("PlayerPanel was not found.");
    private Border PlayerSurfaceHost => _playerSurfaceHost ??= this.FindControl<Border>(nameof(PlayerSurfaceHost))
        ?? throw new InvalidOperationException("PlayerSurfaceHost was not found.");
    private Grid PlayerSurfaceLayer => _playerSurfaceLayer ??= this.FindControl<Grid>(nameof(PlayerSurfaceLayer))
        ?? throw new InvalidOperationException("PlayerSurfaceLayer was not found.");
    private Grid PlayerVideoPresenter => _playerVideoPresenter ??= this.FindControl<Grid>(nameof(PlayerVideoPresenter))
        ?? throw new InvalidOperationException("PlayerVideoPresenter was not found.");
    private Border EventsPanel => this.FindControl<Border>(nameof(EventsPanel))
        ?? throw new InvalidOperationException("EventsPanel was not found.");
    private GridSplitter EventsPanelSplitter => this.FindControl<GridSplitter>(nameof(EventsPanelSplitter))
        ?? throw new InvalidOperationException("EventsPanelSplitter was not found.");
    private Button EventsSplitterAddWindowButton => this.FindControl<Button>(nameof(EventsSplitterAddWindowButton))
        ?? throw new InvalidOperationException("EventsSplitterAddWindowButton was not found.");
    private Border AnalysisPanel => this.FindControl<Border>(nameof(AnalysisPanel))
        ?? throw new InvalidOperationException("AnalysisPanel was not found.");
    private GridSplitter AnalysisPanelSplitter => this.FindControl<GridSplitter>(nameof(AnalysisPanelSplitter))
        ?? throw new InvalidOperationException("AnalysisPanelSplitter was not found.");
    private Button AnalysisSplitterAddWindowButton => this.FindControl<Button>(nameof(AnalysisSplitterAddWindowButton))
        ?? throw new InvalidOperationException("AnalysisSplitterAddWindowButton was not found.");
    private Border BroadcastPanel => this.FindControl<Border>(nameof(BroadcastPanel))
        ?? throw new InvalidOperationException("BroadcastPanel was not found.");
    private Border BroadcastSurfaceHost => this.FindControl<Border>(nameof(BroadcastSurfaceHost))
        ?? throw new InvalidOperationException("BroadcastSurfaceHost was not found.");
    private Grid BroadcastVideoPresenter => _broadcastVideoPresenter ??= this.FindControl<Grid>(nameof(BroadcastVideoPresenter))
        ?? throw new InvalidOperationException("BroadcastVideoPresenter was not found.");
    private TextBlock BroadcastStatusText => this.FindControl<TextBlock>(nameof(BroadcastStatusText))
        ?? throw new InvalidOperationException("BroadcastStatusText was not found.");
    private Border TimelinePanel => this.FindControl<Border>(nameof(TimelinePanel))
        ?? throw new InvalidOperationException("TimelinePanel was not found.");
    private Border SlotPlaceholder0 => this.FindControl<Border>(nameof(SlotPlaceholder0))
        ?? throw new InvalidOperationException("SlotPlaceholder0 was not found.");
    private Border SlotPlaceholder1 => this.FindControl<Border>(nameof(SlotPlaceholder1))
        ?? throw new InvalidOperationException("SlotPlaceholder1 was not found.");
    private Border SlotPlaceholder2 => this.FindControl<Border>(nameof(SlotPlaceholder2))
        ?? throw new InvalidOperationException("SlotPlaceholder2 was not found.");
    private Border SlotPlaceholder3 => this.FindControl<Border>(nameof(SlotPlaceholder3))
        ?? throw new InvalidOperationException("SlotPlaceholder3 was not found.");
    private Border SlotPlaceholder4 => this.FindControl<Border>(nameof(SlotPlaceholder4))
        ?? throw new InvalidOperationException("SlotPlaceholder4 was not found.");
    private GridSplitter TimelinePanelSplitter => this.FindControl<GridSplitter>(nameof(TimelinePanelSplitter))
        ?? throw new InvalidOperationException("TimelinePanelSplitter was not found.");
    private Button TimelineSplitterAddWindowButton => this.FindControl<Button>(nameof(TimelineSplitterAddWindowButton))
        ?? throw new InvalidOperationException("TimelineSplitterAddWindowButton was not found.");
    private ScrollViewer TimelineHorizontalScrollViewer => this.FindControl<ScrollViewer>(nameof(TimelineHorizontalScrollViewer))
        ?? throw new InvalidOperationException("TimelineHorizontalScrollViewer was not found.");
    private Grid TimelineCanvasRoot => this.FindControl<Grid>(nameof(TimelineCanvasRoot))
        ?? throw new InvalidOperationException("TimelineCanvasRoot was not found.");
    private Grid MainLayoutGrid => this.FindControl<Grid>(nameof(MainLayoutGrid))
        ?? throw new InvalidOperationException("MainLayoutGrid was not found.");
    private ColumnDefinition LeftPanelColumn => MainLayoutGrid.ColumnDefinitions[0];
    private ColumnDefinition EventsPanelSplitterColumn => MainLayoutGrid.ColumnDefinitions[1];
    private ColumnDefinition EventsPanelColumn => MainLayoutGrid.ColumnDefinitions[2];
    private ColumnDefinition AnalysisPanelSplitterColumn => MainLayoutGrid.ColumnDefinitions[3];
    private ColumnDefinition AnalysisPanelColumn => MainLayoutGrid.ColumnDefinitions[4];
    private RowDefinition TopContentRow => MainLayoutGrid.RowDefinitions[0];
    private RowDefinition TimelineSplitterRow => MainLayoutGrid.RowDefinitions[1];
    private RowDefinition TimelineRow => MainLayoutGrid.RowDefinitions[2];
    private Button PlayerDetachButton => _playerDetachButton ??= this.FindControl<Button>(nameof(PlayerDetachButton))
        ?? throw new InvalidOperationException("PlayerDetachButton was not found.");
    private Border VideoZoomDiagnosticsOverlay => _videoZoomDiagnosticsOverlay ??= this.FindControl<Border>(nameof(VideoZoomDiagnosticsOverlay))
        ?? throw new InvalidOperationException("VideoZoomDiagnosticsOverlay was not found.");
    private TextBlock VideoZoomDiagnosticsText => _videoZoomDiagnosticsText ??= this.FindControl<TextBlock>(nameof(VideoZoomDiagnosticsText))
        ?? throw new InvalidOperationException("VideoZoomDiagnosticsText was not found.");
    private ToggleButton SpeedMenuButton => _speedMenuButton ??= this.FindControl<ToggleButton>(nameof(SpeedMenuButton))
        ?? throw new InvalidOperationException("SpeedMenuButton was not found.");
    private Grid SeekBarRoot => _seekBarRoot ??= this.FindControl<Grid>(nameof(SeekBarRoot))
        ?? throw new InvalidOperationException("SeekBarRoot was not found.");
    private Border SeekBarProgress => _seekBarProgress ??= this.FindControl<Border>(nameof(SeekBarProgress))
        ?? throw new InvalidOperationException("SeekBarProgress was not found.");
    private Ellipse SeekBarThumb => _seekBarThumb ??= this.FindControl<Ellipse>(nameof(SeekBarThumb))
        ?? throw new InvalidOperationException("SeekBarThumb was not found.");
    private Grid VolumeBarRoot => _volumeBarRoot ??= this.FindControl<Grid>(nameof(VolumeBarRoot))
        ?? throw new InvalidOperationException("VolumeBarRoot was not found.");
    private Border VolumeBarProgress => _volumeBarProgress ??= this.FindControl<Border>(nameof(VolumeBarProgress))
        ?? throw new InvalidOperationException("VolumeBarProgress was not found.");
    private Ellipse VolumeBarThumb => _volumeBarThumb ??= this.FindControl<Ellipse>(nameof(VolumeBarThumb))
        ?? throw new InvalidOperationException("VolumeBarThumb was not found.");
    private Border TimelineZoomSliderRoot => _timelineZoomSliderRoot ??= this.FindControl<Border>(nameof(TimelineZoomSliderRoot))
        ?? throw new InvalidOperationException("TimelineZoomSliderRoot was not found.");
    private Border TimelineZoomSliderProgress => _timelineZoomSliderProgress ??= this.FindControl<Border>(nameof(TimelineZoomSliderProgress))
        ?? throw new InvalidOperationException("TimelineZoomSliderProgress was not found.");
    private Ellipse TimelineZoomSliderThumb => _timelineZoomSliderThumb ??= this.FindControl<Ellipse>(nameof(TimelineZoomSliderThumb))
        ?? throw new InvalidOperationException("TimelineZoomSliderThumb was not found.");
    private Border VolumePopupRoot => _volumePopupRoot ??= this.FindControl<Border>(nameof(VolumePopupRoot))
        ?? throw new InvalidOperationException("VolumePopupRoot was not found.");
    private Border PresetEditorDialog => this.FindControl<Border>(nameof(PresetEditorDialog))
        ?? throw new InvalidOperationException("PresetEditorDialog was not found.");
    private Button PresetEditorCloseButton => this.FindControl<Button>(nameof(PresetEditorCloseButton))
        ?? throw new InvalidOperationException("PresetEditorCloseButton was not found.");
    private Border TagEventEditorDialog => this.FindControl<Border>(nameof(TagEventEditorDialog))
        ?? throw new InvalidOperationException("TagEventEditorDialog was not found.");
    private Button TagEventEditorCloseButton => this.FindControl<Button>(nameof(TagEventEditorCloseButton))
        ?? throw new InvalidOperationException("TagEventEditorCloseButton was not found.");
    private Button StartupPrimaryButton => this.FindControl<Button>(nameof(StartupPrimaryButton))
        ?? throw new InvalidOperationException("StartupPrimaryButton was not found.");
    private Button NewProjectCloseButton => this.FindControl<Button>(nameof(NewProjectCloseButton))
        ?? throw new InvalidOperationException("NewProjectCloseButton was not found.");
    private Button ExportDialogCloseButton => this.FindControl<Button>(nameof(ExportDialogCloseButton))
        ?? throw new InvalidOperationException("ExportDialogCloseButton was not found.");

    private MainWindowViewModel? _viewModel;
    private Border? _playerSurfaceHost;
    private Grid? _playerSurfaceLayer;
    private Grid? _playerVideoPresenter;
#if WINDOWS_MPV
    private MpvView? _playerView;
#endif
    private VideoView? _playerLibVlcView;
    private Button? _playerDetachButton;
    private Border? _videoZoomDiagnosticsOverlay;
    private TextBlock? _videoZoomDiagnosticsText;
    private ToggleButton? _speedMenuButton;
    private Grid? _seekBarRoot;
    private Border? _seekBarProgress;
    private Ellipse? _seekBarThumb;
    private Grid? _volumeBarRoot;
    private Border? _volumeBarProgress;
    private Ellipse? _volumeBarThumb;
    private Border? _timelineZoomSliderRoot;
    private Border? _timelineZoomSliderProgress;
    private Ellipse? _timelineZoomSliderThumb;
    private Border? _volumePopupRoot;
    private Grid? _broadcastVideoPresenter;
#if WINDOWS_MPV
    private MpvView? _broadcastView;
#endif
    private VideoView? _broadcastLibVlcView;
    private readonly IMediaPlaybackService _broadcastPlaybackService;
    private bool _isSynchronizingMenus;
    private bool _isSeekDragging;
    private bool _isVolumeDragging;
    private bool _isTimelineZoomSliderDragging;
    private bool _isVideoPanDragging;
    private bool _isVideoZoomDiagnosticsVisible;
#if WINDOWS_MPV
    private bool _isPlayerRendererInitialized;
    private bool _isPlayerRendererInitializationQueued;
    private int _playerRendererAttachAttempts;
    private bool _isBroadcastRendererInitialized;
    private bool _isBroadcastRendererInitializationQueued;
    private int _broadcastRendererAttachAttempts;
#endif
    private bool _isBroadcastLiveStarted;
    private bool _isBroadcastManuallyStopped;
    private bool _isRecoveringBroadcastLivePreview;
    private int _broadcastLiveRecoveryVersion;
    private bool _broadcastLiveRecoveryForceRefresh;
    private long _broadcastLiveHealthFrame;
    private long _broadcastLiveHealthTimestamp;
    private double _broadcastLiveLagSeconds;
    private readonly DispatcherTimer _broadcastLiveEdgeTimer;
    private bool _isTimelinePanDragging;
    private bool _isTimelineSeekDragging;
    private bool _hasTimelineSeekMoved;
    private bool _isWorkspacePanelDragging;
    private int _workspacePanelAnimationVersion;
    private bool _isAdjustingEventTypeHotkeyText;
    private bool _isPlayerFullscreen;
    private bool _isPlayerPanelHidden;
    private bool _isDockingDetachedPlayer;
    private string? _maximizedPanel;
    private double _lastVisibleLeftPanelWidth;
    private Point _workspacePanelDragStartPoint;
    private WorkspaceEntityKind? _draggedWorkspaceEntity;
    private WorkspaceEntityKind? _workspacePanelDragVisualEntity;
    private Border? _workspacePanelDropTargetBorder;
    private Button? _visibleWorkspaceAddButton;
    private Button? _hoveredWorkspaceAddButton;
    private int _workspacePanelDragSourceSlot = -1;
    private int _workspacePanelDropTargetSlot = -1;
    private WorkspaceLayoutKind _workspaceLayoutKind = WorkspaceLayoutKind.Reference;
    private readonly Dictionary<WorkspaceEntityKind, int> _entitySlots = new()
    {
        [WorkspaceEntityKind.Player] = 0,
        [WorkspaceEntityKind.Timeline] = 1,
        [WorkspaceEntityKind.EventsClips] = 2,
        [WorkspaceEntityKind.Statistics] = 3
    };
    private WindowState _windowStateBeforePlayerFullscreen = WindowState.Maximized;
    private Thickness _mainLayoutMarginBeforePlayerFullscreen = new(14);
    private Point _lastVideoPanPoint;
    private Point _lastVideoZoomFocus = new(0.5d, 0.5d);
    private IntPtr _mainWindowHandle;
    private IntPtr _windowInputHookHandle;
    private IntPtr _previousWindowInputProc;
    private NativeWindowProc? _windowInputProc;
    private IntPtr _videoWheelHookHandle;
    private IntPtr _previousVideoWindowProc;
    private NativeWindowProc? _videoWindowProc;
    private IDisposable? _playerContextSubscription;
    private IDisposable? _broadcastContextSubscription;
    private Window? _detachedPlayerWindow;
    private Control? _dockedPlayerContent;
    private WindowState _detachedPlayerWindowStateBeforeFullscreen = WindowState.Normal;
    private long _lastVideoWheelTick;
    private double _lastVideoWheelDelta;
    private PixelPoint _lastVideoWheelScreenPoint;
    private ulong _gestureZoomStartDistance;
    private double _gestureZoomStartLevel = 1d;

    private enum WorkspaceLayoutKind
    {
        Single,
        Reference,
        SplitRows,
        TopTwoBottom,
        FourGrid,
        SplitColumns,
        BottomTwo,
        FiveWindows
    }

    private enum WorkspaceEntityKind
    {
        Player,
        Timeline,
        EventsClips,
        Statistics,
        Broadcast
    }

    private readonly record struct WorkspaceSlotBounds(int Row, int Column, int RowSpan, int ColumnSpan);

    private static readonly WorkspaceEntityKind[] WorkspaceEntityOrder =
    [
        WorkspaceEntityKind.Player,
        WorkspaceEntityKind.Timeline,
        WorkspaceEntityKind.EventsClips,
        WorkspaceEntityKind.Statistics,
        WorkspaceEntityKind.Broadcast
    ];

    private delegate IntPtr NativeWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GestureInfo
    {
        public uint Size;
        public uint Flags;
        public uint Id;
        public IntPtr Target;
        public short LocationX;
        public short LocationY;
        public uint InstanceId;
        public uint SequenceId;
        public ulong Arguments;
        public uint ExtraArgs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GestureConfig
    {
        public uint Id;
        public uint Want;
        public uint Block;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr regionHandle, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGestureInfo(IntPtr gestureInfo, ref GestureInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseGestureInfoHandle(IntPtr gestureInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetGestureConfig(
        IntPtr hWnd,
        uint reserved,
        uint count,
        [In] GestureConfig[] configs,
        uint size);

    public MainWindow()
    {
        InitializeComponent();
        _broadcastPlaybackService = CreateBroadcastPlaybackService();
        InitializePlaybackViews();
        _broadcastLiveEdgeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _broadcastLiveEdgeTimer.Tick += OnBroadcastLiveEdgeTimerTick;
        _broadcastLiveEdgeTimer.Start();
        DataContextChanged += OnDataContextChanged;
        LayoutUpdated += OnLayoutUpdated;
        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(
            InputElement.PointerMovedEvent,
            OnWorkspacePanelDragPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerReleasedEvent,
            OnWorkspacePanelDragPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnWindowPointerWheelChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlayerPanel.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPlayerSurfacePointerWheelChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlayerSurfaceHost.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPlayerSurfacePointerWheelChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        UpdatePlayerDetachButtonState();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private static bool UseMpvEmbeddedPlayback =>
#if WINDOWS_MPV
        OperatingSystem.IsWindows();
#else
        false;
#endif

    private static IMediaPlaybackService CreateBroadcastPlaybackService()
    {
#if WINDOWS_MPV
        return UseMpvEmbeddedPlayback
            ? new MpvMediaPlaybackService()
            : new LibVlcMediaPlaybackService();
#else
        return new LibVlcMediaPlaybackService();
#endif
    }

    private void InitializePlaybackViews()
    {
#if WINDOWS_MPV
        if (UseMpvEmbeddedPlayback)
        {
            var playerView = CreateMpvView("PlayerView", OnPlayerViewInitialized);
            _playerContextSubscription = playerView.GetObservable(MpvView.MpvContextProperty)
                .Subscribe(_ => QueuePlayerRendererInitialization());
            PlayerVideoPresenter.Children.Insert(0, playerView);
            _playerView = playerView;

            var broadcastView = CreateMpvView("BroadcastView", OnBroadcastViewInitialized);
            _broadcastContextSubscription = broadcastView.GetObservable(MpvView.MpvContextProperty)
                .Subscribe(_ => QueueBroadcastRendererInitialization());
            BroadcastVideoPresenter.Children.Insert(0, broadcastView);
            _broadcastView = broadcastView;
            return;
        }
#endif

        _playerLibVlcView = CreateLibVlcView();
        PlayerVideoPresenter.Children.Insert(0, _playerLibVlcView);

        _broadcastLibVlcView = CreateLibVlcView();
        BroadcastVideoPresenter.Children.Insert(0, _broadcastLibVlcView);
    }

#if WINDOWS_MPV
    private MpvView CreateMpvView(string name, EventHandler initializedHandler)
    {
        var view = new MpvView
        {
            Name = name,
            Renderer = VideoRenderer.OpenGl,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        view.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPlayerSurfacePointerWheelChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        view.ViewInitialized += initializedHandler;
        return view;
    }
#endif

    private static VideoView CreateLibVlcView()
    {
        return new VideoView
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
    }

    private void BindManagedVideoViews()
    {
        if (_playerLibVlcView is not null)
        {
            _playerLibVlcView.MediaPlayer = _viewModel?.MediaPlayer;
        }

        if (_broadcastLibVlcView is not null)
        {
            _broadcastLibVlcView.MediaPlayer = (_broadcastPlaybackService as LibVlcMediaPlaybackService)?.MediaPlayer;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        BindManagedVideoViews();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ConfigureBroadcastEntityAvailability();
            UpdateWorkspaceEntityVisibility();
            UpdateVideoSurfaceVisibility();
            UpdatePanelLayout();
            UpdateVideoZoomLayout();
            UpdateSeekBarVisuals();
            UpdateVolumeBarVisuals();
            UpdateTimelineZoomSliderVisuals();
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            QueuePlayerRendererInitialization();
            UpdateVideoSurfaceVisibility();
            UpdatePanelLayout();
            TryHookWindowInput();
            await _viewModel.InitializeCommand.ExecuteAsync(null);
            UpdateWorkspaceEntityVisibility();
            UpdateVideoSurfaceVisibility();
            UpdatePanelLayout();
            UpdateVideoZoomLayout();
            TryHookWindowInput();
            UpdateSeekBarVisuals();
            UpdateVolumeBarVisuals();
            UpdateTimelineZoomSliderVisuals();
            ResetTimelineScrollIfNeeded(force: true);
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        TryBindEmbeddedVideoOutput();

        if (_viewModel?.TimelineCurrentFrame == 0 && TimelineHorizontalScrollViewer.Offset.X != 0)
        {
            TimelineHorizontalScrollViewer.Offset = new Vector(0, TimelineHorizontalScrollViewer.Offset.Y);
        }
    }

    private void UpdateVideoSurfaceVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        QueuePlayerRendererInitialization();

        var isVisible = !_isPlayerPanelHidden;
        PlayerSurfaceHost.IsVisible = isVisible;
#if WINDOWS_MPV
        if (_playerView is not null)
        {
            _playerView.IsVisible = isVisible;
        }
#endif

        if (_playerLibVlcView is not null)
        {
            _playerLibVlcView.IsVisible = isVisible;
        }

        if (!isVisible)
        {
            return;
        }

        UpdateVideoZoomLayout();
    }

#if WINDOWS_MPV
    private void QueuePlayerRendererInitialization(bool waitForRender = false)
    {
        if (!UseMpvEmbeddedPlayback || _playerView is null)
        {
            return;
        }

        if (_isPlayerRendererInitialized || _isPlayerRendererInitializationQueued)
        {
            return;
        }

        _isPlayerRendererInitializationQueued = true;
        _ = RunQueuedPlayerRendererInitializationAsync(waitForRender);
    }

    private async Task RunQueuedPlayerRendererInitializationAsync(bool waitForRender)
    {
        if (waitForRender)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                _isPlayerRendererInitializationQueued = false;
                EnsurePlayerRendererInitialized();
            },
            DispatcherPriority.Loaded);
    }

    private void EnsurePlayerRendererInitialized()
    {
        if (!UseMpvEmbeddedPlayback || _playerView is null)
        {
            return;
        }

        if (_isPlayerRendererInitialized)
        {
            AttachPlayerMpvContext();
            return;
        }

        try
        {
            var playerView = _playerView!;
            if (!playerView.IsAttachedToVisualTree()
                || PlayerSurfaceHost.Bounds.Width <= 1
                || PlayerSurfaceHost.Bounds.Height <= 1)
            {
                QueuePlayerRendererInitialization(waitForRender: true);
                return;
            }

            var mpvContext = playerView.MpvContext;
            if (mpvContext is null)
            {
                if (_playerRendererAttachAttempts++ >= 2)
                {
                    playerView.InitRenderer();
                    mpvContext = playerView.MpvContext;
                }
                else
                {
                    QueuePlayerRendererInitialization(waitForRender: true);
                    return;
                }
            }

            if (mpvContext is null)
            {
                QueuePlayerRendererInitialization(waitForRender: true);
                return;
            }

            if (!IsMpvCustomRenderingReady(mpvContext))
            {
                if (_playerRendererAttachAttempts++ < 120)
                {
                    QueuePlayerRendererInitialization(waitForRender: true);
                    return;
                }

                Trace.TraceWarning("mpv custom rendering was not ready before timeout; attaching context as fallback.");
            }
            else
            {
                _playerRendererAttachAttempts = 0;
            }

            ConfigureEmbeddedMpvRenderer(mpvContext);
            _isPlayerRendererInitialized = true;
            AttachPlayerMpvContext();
            UpdateVideoZoomLayout();
        }
        catch (Exception ex)
        {
            _isPlayerRendererInitialized = false;
            Trace.TraceWarning($"mpv renderer initialization failed: {ex}");
            QueuePlayerRendererInitialization(waitForRender: true);
        }
    }

    private static bool IsMpvCustomRenderingReady(MpvContext mpvContext)
    {
        try
        {
            return mpvContext.IsCustomRendering();
        }
        catch
        {
            return false;
        }
    }

    private void AttachPlayerMpvContext()
    {
        if (!_isPlayerRendererInitialized
            || _viewModel is null
            || _playerView?.MpvContext is not { } mpvContext)
        {
            return;
        }

        _viewModel.AttachMpvContext(mpvContext);
    }

    private void OnPlayerViewInitialized(object? sender, EventArgs e)
    {
        QueuePlayerRendererInitialization();
    }

    private void QueueBroadcastRendererInitialization(bool waitForRender = false)
    {
        if (!UseMpvEmbeddedPlayback || _broadcastView is null)
        {
            return;
        }

        if (_isBroadcastRendererInitialized || _isBroadcastRendererInitializationQueued)
        {
            return;
        }

        _isBroadcastRendererInitializationQueued = true;
        _ = RunQueuedBroadcastRendererInitializationAsync(waitForRender);
    }

    private async Task RunQueuedBroadcastRendererInitializationAsync(bool waitForRender)
    {
        if (waitForRender)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                _isBroadcastRendererInitializationQueued = false;
                EnsureBroadcastRendererInitialized();
            },
            DispatcherPriority.Loaded);
    }

    private void EnsureBroadcastRendererInitialized()
    {
        if (!UseMpvEmbeddedPlayback || _broadcastView is null)
        {
            return;
        }

        if (_isBroadcastRendererInitialized)
        {
            AttachBroadcastMpvContext();
            return;
        }

        try
        {
            var broadcastView = _broadcastView!;
            if (!broadcastView.IsAttachedToVisualTree()
                || BroadcastSurfaceHost.Bounds.Width <= 1
                || BroadcastSurfaceHost.Bounds.Height <= 1)
            {
                QueueBroadcastRendererInitialization(waitForRender: true);
                return;
            }

            var mpvContext = broadcastView.MpvContext;
            if (mpvContext is null)
            {
                if (_broadcastRendererAttachAttempts++ >= 2)
                {
                    broadcastView.InitRenderer();
                    mpvContext = broadcastView.MpvContext;
                }
                else
                {
                    QueueBroadcastRendererInitialization(waitForRender: true);
                    return;
                }
            }

            if (mpvContext is null)
            {
                QueueBroadcastRendererInitialization(waitForRender: true);
                return;
            }

            if (!IsMpvCustomRenderingReady(mpvContext))
            {
                if (_broadcastRendererAttachAttempts++ < 120)
                {
                    QueueBroadcastRendererInitialization(waitForRender: true);
                    return;
                }

                Trace.TraceWarning("broadcast mpv custom rendering was not ready before timeout; attaching context as fallback.");
            }
            else
            {
                _broadcastRendererAttachAttempts = 0;
            }

            ConfigureEmbeddedMpvRenderer(mpvContext);
            _isBroadcastRendererInitialized = true;
            AttachBroadcastMpvContext();
        }
        catch (Exception ex)
        {
            _isBroadcastRendererInitialized = false;
            Trace.TraceWarning($"broadcast mpv renderer initialization failed: {ex}");
            QueueBroadcastRendererInitialization(waitForRender: true);
        }
    }

    private void AttachBroadcastMpvContext()
    {
        if (!_isBroadcastRendererInitialized
            || _broadcastView?.MpvContext is not { } mpvContext)
        {
            return;
        }

        if (_broadcastPlaybackService is MpvMediaPlaybackService mpvPlaybackService)
        {
            mpvPlaybackService.AttachMpvContext(mpvContext);
        }

        _ = EnsureBroadcastDisplayAsync();
    }

    private void OnBroadcastViewInitialized(object? sender, EventArgs e)
    {
        QueueBroadcastRendererInitialization();
    }
#else
    private void QueuePlayerRendererInitialization(bool waitForRender = false)
    {
    }

    private void QueueBroadcastRendererInitialization(bool waitForRender = false)
    {
    }
#endif

    private async Task OpenBroadcastLiveStreamAsync(string source, string metadataPath, CancellationToken cancellationToken)
    {
        switch (_broadcastPlaybackService)
        {
#if WINDOWS_MPV
            case MpvMediaPlaybackService mpvPlaybackService:
                await mpvPlaybackService.OpenLiveStreamAsync(source, metadataPath, cancellationToken);
                break;
#endif
            case LibVlcMediaPlaybackService libVlcPlaybackService:
                await libVlcPlaybackService.OpenLiveStreamAsync(source, metadataPath, cancellationToken);
                BindManagedVideoViews();
                break;
            default:
                throw new InvalidOperationException("Broadcast playback service does not support live streams.");
        }
    }

    private bool DropBroadcastLiveBuffers()
    {
#if WINDOWS_MPV
        return _broadcastPlaybackService is MpvMediaPlaybackService mpvPlaybackService
            && mpvPlaybackService.DropLiveBuffers();
#else
        return false;
#endif
    }

    private async Task EnsureBroadcastLiveAsync()
    {
        if (_viewModel?.IsBroadcastModeProject != true
            || !BroadcastPanel.IsVisible
            || _isBroadcastManuallyStopped
            || _isRecoveringBroadcastLivePreview
            || _isBroadcastLiveStarted)
        {
            return;
        }

        try
        {
            BroadcastStatusText.Text = "Запускаем live-DVR...";
            var previewSource = await _viewModel.EnsureBroadcastDvrAsync(CancellationToken.None);
            await OpenBroadcastLiveStreamAsync(
                previewSource,
                "dvr://broadcast",
                CancellationToken.None);
            _broadcastPlaybackService.Play();
            _isBroadcastLiveStarted = true;
            ResetBroadcastLiveEdgeTracking();
            BroadcastStatusText.Text = "LIVE DVR";
        }
        catch (Exception ex)
        {
            _isBroadcastLiveStarted = false;
            BroadcastStatusText.Text = $"Live-DVR недоступен: {ex.Message}";
        }
    }

    private async Task EnsureBroadcastDisplayAsync()
    {
        await EnsureBroadcastLiveAsync();
    }

    private void QueueBroadcastLivePreviewRecovery(bool forceRefresh = false)
    {
        if (_viewModel?.IsBroadcastDvrRunning != true
            || !_isBroadcastLiveStarted
            || _isBroadcastManuallyStopped
            || !BroadcastPanel.IsVisible)
        {
            return;
        }

        _broadcastLiveRecoveryForceRefresh |= forceRefresh;
        var version = ++_broadcastLiveRecoveryVersion;
        _ = RecoverBroadcastLivePreviewAsync(version);
    }

    private async Task RecoverBroadcastLivePreviewAsync(int version)
    {
        await Task.Delay(140).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (version != _broadcastLiveRecoveryVersion
                    || _viewModel?.IsBroadcastDvrRunning != true
                    || !_isBroadcastLiveStarted
                    || _isBroadcastManuallyStopped
                    || !BroadcastPanel.IsVisible)
                {
                    return;
                }

                if (_isTimelinePanDragging)
                {
                    QueueBroadcastLivePreviewRecovery(forceRefresh: _broadcastLiveRecoveryForceRefresh);
                    return;
                }

                var shouldForceRefresh = _broadcastLiveRecoveryForceRefresh;
                _broadcastLiveRecoveryForceRefresh = false;
                if (shouldForceRefresh || ShouldRefreshBroadcastLivePreview())
                {
                    _ = RefreshBroadcastLivePreviewAsync();
                }
            },
            DispatcherPriority.Background);
    }

    private bool ShouldRefreshBroadcastLivePreview()
    {
        if (!_broadcastPlaybackService.IsPlaying)
        {
            return true;
        }

        var currentTimestamp = Stopwatch.GetTimestamp();
        if (_broadcastLiveHealthTimestamp == 0)
        {
            _broadcastLiveHealthFrame = _broadcastPlaybackService.CurrentFrame;
            _broadcastLiveHealthTimestamp = currentTimestamp;
            return false;
        }

        var elapsed = Stopwatch.GetElapsedTime(_broadcastLiveHealthTimestamp, currentTimestamp);
        if (elapsed < TimeSpan.FromMilliseconds(150))
        {
            return false;
        }

        var framesPerSecond = Math.Max(1d, _broadcastPlaybackService.FramesPerSecond);
        var currentFrame = _broadcastPlaybackService.CurrentFrame;
        var actualAdvanceSeconds = Math.Max(0L, currentFrame - _broadcastLiveHealthFrame) / framesPerSecond;
        var measuredLostSeconds = elapsed.TotalSeconds - actualAdvanceSeconds;
        var lostSeconds = Math.Abs(measuredLostSeconds) < 0.075d ? 0d : measuredLostSeconds;

        _broadcastLiveLagSeconds = Math.Clamp(_broadcastLiveLagSeconds + lostSeconds, 0d, 5d);
        _broadcastLiveHealthFrame = currentFrame;
        _broadcastLiveHealthTimestamp = currentTimestamp;

        return _broadcastLiveLagSeconds > 0.18d;
    }

    private void OnBroadcastLiveEdgeTimerTick(object? sender, EventArgs e)
    {
        if (_viewModel?.IsBroadcastDvrRunning != true
            || !_isBroadcastLiveStarted
            || _isBroadcastManuallyStopped
            || !BroadcastPanel.IsVisible)
        {
            ClearBroadcastLiveEdgeTracking();
            return;
        }

        if (_isRecoveringBroadcastLivePreview || _isTimelinePanDragging)
        {
            return;
        }

        if (ShouldRefreshBroadcastLivePreview())
        {
            _ = RefreshBroadcastLivePreviewAsync();
        }
    }

    private void ResetBroadcastLiveEdgeTracking()
    {
        _broadcastLiveHealthFrame = _broadcastPlaybackService.CurrentFrame;
        _broadcastLiveHealthTimestamp = Stopwatch.GetTimestamp();
        _broadcastLiveLagSeconds = 0d;
    }

    private void ClearBroadcastLiveEdgeTracking()
    {
        _broadcastLiveHealthFrame = 0;
        _broadcastLiveHealthTimestamp = 0;
        _broadcastLiveLagSeconds = 0d;
        _broadcastLiveRecoveryForceRefresh = false;
    }

    private async Task RefreshBroadcastLivePreviewAsync()
    {
        if (_isRecoveringBroadcastLivePreview
            || _viewModel?.IsBroadcastDvrRunning != true
            || string.IsNullOrWhiteSpace(_viewModel.BroadcastDvrPreviewSource)
            || !BroadcastPanel.IsVisible)
        {
            return;
        }

        _isRecoveringBroadcastLivePreview = true;
        _broadcastLiveRecoveryForceRefresh = false;
        var previewSource = _viewModel.BroadcastDvrPreviewSource;
        try
        {
            if (DropBroadcastLiveBuffers())
            {
                _broadcastPlaybackService.Play();
                _isBroadcastLiveStarted = true;
                ResetBroadcastLiveEdgeTracking();
                BroadcastStatusText.Text = "LIVE DVR";
                return;
            }

            BroadcastStatusText.Text = "Обновляем LIVE...";
            _broadcastPlaybackService.Close();
            _isBroadcastLiveStarted = false;
            await OpenBroadcastLiveStreamAsync(
                previewSource,
                "dvr://broadcast",
                CancellationToken.None);
            _broadcastPlaybackService.Play();
            _isBroadcastLiveStarted = true;
            ResetBroadcastLiveEdgeTracking();
            BroadcastStatusText.Text = "LIVE DVR";
        }
        catch (Exception ex)
        {
            _isBroadcastLiveStarted = false;
            BroadcastStatusText.Text = $"Live-DVR не восстановлен: {ex.Message}";
        }
        finally
        {
            _isRecoveringBroadcastLivePreview = false;
        }
    }

    private void StopBroadcastLive(bool stopTimeline = true)
    {
        if (!_isBroadcastLiveStarted)
        {
            return;
        }

        _broadcastPlaybackService.Close();
        _isBroadcastLiveStarted = false;
        ClearBroadcastLiveEdgeTracking();
        BroadcastStatusText.Text = "Ожидание камеры";
    }

    private async Task StopBroadcastLiveAsync(bool stopDvr, bool manualStop)
    {
        _broadcastPlaybackService.Close();
        _isBroadcastLiveStarted = false;
        ClearBroadcastLiveEdgeTracking();

        if (manualStop)
        {
            _isBroadcastManuallyStopped = true;
        }

        if (stopDvr && _viewModel is not null)
        {
            await _viewModel.StopBroadcastDvrAsync(CancellationToken.None);
        }

        BroadcastStatusText.Text = manualStop ? "Трансляция остановлена" : "Ожидание камеры";
    }

#if WINDOWS_MPV
    private static void ConfigureEmbeddedMpvRenderer(MpvContext mpvContext)
    {
        SetMpvOptionString(mpvContext, "config", "no");
        SetMpvOptionString(mpvContext, "force-window", "no");
        SetMpvOptionString(mpvContext, "vo", "libmpv");
        SetMpvOptionString(mpvContext, "osc", "no");
        SetMpvOptionString(mpvContext, "terminal", "no");
        SetMpvOptionString(mpvContext, "input-terminal", "no");
    }

    private static void SetMpvOptionString(MpvContext mpvContext, string name, string value)
    {
        try
        {
            mpvContext.SetOptionString(name, value);
        }
        catch
        {
        }
    }
#endif

    private void TryBindEmbeddedVideoOutput()
    {
    }

    private async void OnBroadcastRecordingButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!_viewModel.IsBroadcastDvrRunning)
        {
            await EnsureBroadcastDisplayAsync();
        }

        if (!_viewModel.CanUseBroadcastRecording && !_viewModel.IsBroadcastRecording)
        {
            return;
        }

        await _viewModel.ToggleBroadcastRecordingCommand.ExecuteAsync(null);
        await EnsureBroadcastDisplayAsync();
    }

    private async void OnBroadcastLiveButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanToggleBroadcastLive)
        {
            return;
        }

        if (_viewModel.IsBroadcastDvrRunning)
        {
            await StopBroadcastLiveAsync(stopDvr: true, manualStop: true);
            return;
        }

        _isBroadcastManuallyStopped = false;
        await EnsureBroadcastDisplayAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _broadcastLiveEdgeTimer.Stop();
        DockPlayerToMainWindow();
        _viewModel?.ShutdownBroadcastRecording();
        _playerContextSubscription?.Dispose();
        _playerContextSubscription = null;
        _broadcastContextSubscription?.Dispose();
        _broadcastContextSubscription = null;
        if (_broadcastPlaybackService is IDisposable disposableBroadcastPlayback)
        {
            disposableBroadcastPlayback.Dispose();
        }
        UnhookWindowInput();
        UnhookVideoWheel();
    }

    private async void OnFileMenuActionClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: string tag })
        {
            switch (tag)
            {
                case "NewProject":
                    _viewModel.OpenNewProjectDialogCommand.Execute(null);
                    break;
                case "OpenProjects":
                    await _viewModel.OpenStartupScreenCommand.ExecuteAsync(null);
                    break;
                case "Export":
                    _viewModel.OpenExportDialogCommand.Execute(null);
                    break;
            }
        }

        FileMenuButton.IsChecked = false;
    }

    private void OnViewMenuActionClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button button)
        {
            switch (button.Tag as string)
            {
                case "TogglePlayerPanel":
                    ToggleEntityVisibility(WorkspaceEntityKind.Player);
                    break;
                case "ToggleTimelinePanel":
                    ToggleEntityVisibility(WorkspaceEntityKind.Timeline);
                    break;
                case "ToggleEventsPanel":
                    ToggleEntityVisibility(WorkspaceEntityKind.EventsClips);
                    break;
                case "ToggleAnalysisPanel":
                    ToggleEntityVisibility(WorkspaceEntityKind.Statistics);
                    break;
                case "ToggleBroadcastPanel":
                    ToggleEntityVisibility(WorkspaceEntityKind.Broadcast);
                    break;
                case "AddWorkspaceWindow":
                    AddWorkspaceWindow();
                    break;
            }
        }

        ViewMenuButton.IsChecked = false;
    }

    private void OnHelpMenuActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && string.Equals(tag, "OpenLogsFolder", StringComparison.Ordinal))
        {
            OpenLogsFolder();
        }

        HelpMenuButton.IsChecked = false;
    }

    private void OpenLogsFolder()
    {
        try
        {
            AppLogService.InitializeDefault();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLogService.LogsDirectory,
                UseShellExecute = true
            });

            if (_viewModel is not null)
            {
                _viewModel.StatusMessage = $"Логи: {AppLogService.LogsDirectory}";
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error(ex, "Open logs folder failed");
            if (_viewModel is not null)
            {
                _viewModel.StatusMessage = $"Не удалось открыть папку логов: {ex.Message}";
            }
        }
    }

    private void OnFileMenuChecked(object? sender, RoutedEventArgs e)
    {
        CloseOtherMenus(FileMenuButton);
    }

    private void OnMarkupMenuChecked(object? sender, RoutedEventArgs e)
    {
        CloseOtherMenus(MarkupMenuButton);
    }

    private void OnViewMenuChecked(object? sender, RoutedEventArgs e)
    {
        CloseOtherMenus(ViewMenuButton);
    }

    private void OnSettingsMenuChecked(object? sender, RoutedEventArgs e)
    {
        CloseOtherMenus(SettingsMenuButton);
    }

    private void OnHelpMenuChecked(object? sender, RoutedEventArgs e)
    {
        CloseOtherMenus(HelpMenuButton);
    }

    private void OnPanelWindowControlClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':', 2);
        if (parts.Length != 2)
        {
            return;
        }

        var panel = parts[0];
        var action = parts[1];

        switch (action)
        {
            case "Detach" when string.Equals(panel, PanelPlayer, StringComparison.Ordinal):
                TogglePlayerDetached();
                break;
            case "Minimize":
                if (HandleDetachedPlayerWindowControl(panel, action))
                {
                    break;
                }

                MinimizePanel(panel);
                break;
            case "Maximize":
                if (HandleDetachedPlayerWindowControl(panel, action))
                {
                    break;
                }

                TogglePanelMaximized(panel);
                break;
            case "Close":
                if (HandleDetachedPlayerWindowControl(panel, action))
                {
                    break;
                }

                ClosePanel(panel);
                break;
        }
    }

    private bool HandleDetachedPlayerWindowControl(string panel, string action)
    {
        if (!string.Equals(panel, PanelPlayer, StringComparison.Ordinal)
            || _detachedPlayerWindow is not { } window)
        {
            return false;
        }

        switch (action)
        {
            case "Minimize":
                window.WindowState = WindowState.Minimized;
                return true;
            case "Maximize":
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return true;
            case "Close":
                DockPlayerToMainWindow();
                return true;
            default:
                return false;
        }
    }

    private void TogglePlayerDetached()
    {
        if (_detachedPlayerWindow is null)
        {
            DetachPlayerToWindow();
        }
        else
        {
            DockPlayerToMainWindow();
        }
    }

    private void DetachPlayerToWindow()
    {
        if (_detachedPlayerWindow is not null)
        {
            _detachedPlayerWindow.Activate();
            return;
        }

        if (PlayerPanel.Child is not Control content)
        {
            return;
        }

        if (_isPlayerFullscreen)
        {
            _isPlayerFullscreen = false;
            ApplyPlayerFullscreenState();
        }

        _dockedPlayerContent = content;
        PlayerPanel.Child = CreateDetachedPlayerPlaceholder();

        var width = Math.Max(760d, PlayerPanel.Bounds.Width);
        var height = Math.Max(480d, PlayerPanel.Bounds.Height);
        var window = new Window
        {
            Title = "VideoAnalytics - Плеер",
            Width = width,
            Height = height,
            MinWidth = 640,
            MinHeight = 360,
            Background = new SolidColorBrush(Color.Parse("#050B14")),
            Content = content,
            DataContext = DataContext,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        window.Closing += OnDetachedPlayerWindowClosing;
        window.AddHandler(InputElement.KeyDownEvent, OnDetachedPlayerWindowKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyUpEvent, OnDetachedPlayerWindowKeyUp, RoutingStrategies.Tunnel);
        _detachedPlayerWindow = window;
        _detachedPlayerWindowStateBeforeFullscreen = WindowState.Normal;

        UpdatePlayerDetachButtonState();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
        window.Show(this);
        window.Activate();
        RecreatePlayerRendererAfterTopLevelChange();
    }

    private Control CreateDetachedPlayerPlaceholder()
    {
        var restoreButton = new Button
        {
            Content = "Вернуть плеер",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(16, 10),
            MinWidth = 160
        };
        restoreButton.Classes.Add("action-button");
        restoreButton.Click += (_, _) => DockPlayerToMainWindow();

        return new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#050B14")),
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Плеер открыт в отдельном окне",
                            Foreground = new SolidColorBrush(Color.Parse("#D9E5F8")),
                            FontSize = 15,
                            FontWeight = FontWeight.SemiBold,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        },
                        restoreButton
                    }
                }
            }
        };
    }

    private void DockPlayerToMainWindow()
    {
        var window = _detachedPlayerWindow;
        if (window is null)
        {
            return;
        }

        _isDockingDetachedPlayer = true;
        try
        {
            window.Closing -= OnDetachedPlayerWindowClosing;
            window.RemoveHandler(InputElement.KeyDownEvent, OnDetachedPlayerWindowKeyDown);
            window.RemoveHandler(InputElement.KeyUpEvent, OnDetachedPlayerWindowKeyUp);

            var content = window.Content as Control ?? _dockedPlayerContent;
            window.Content = null;
            if (content is not null)
            {
                PlayerPanel.Child = content;
            }

            _detachedPlayerWindow = null;
            _dockedPlayerContent = null;
            _detachedPlayerWindowStateBeforeFullscreen = WindowState.Normal;
            window.Close();
        }
        finally
        {
            _isDockingDetachedPlayer = false;
        }

        UpdatePlayerDetachButtonState();
        UpdateVideoSurfaceVisibility();
        UpdateVideoZoomLayout();
        UpdateSeekBarVisuals();
        UpdateVolumeBarVisuals();
        UpdatePanelLayout();
        RecreatePlayerRendererAfterTopLevelChange();
    }

    private void RecreatePlayerRendererAfterTopLevelChange()
    {
#if WINDOWS_MPV
        if (!UseMpvEmbeddedPlayback || _playerView is null)
        {
            BindManagedVideoViews();
            return;
        }

        _isPlayerRendererInitialized = false;
        _isPlayerRendererInitializationQueued = false;
        _playerRendererAttachAttempts = 0;

        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    ReplacePlayerViewForCurrentTopLevel();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"mpv renderer recreation after window move failed: {ex}");
                }

                QueuePlayerRendererInitialization(waitForRender: true);
            },
            DispatcherPriority.Loaded);
#else
        BindManagedVideoViews();
#endif
    }

#if WINDOWS_MPV
    private void ReplacePlayerViewForCurrentTopLevel()
    {
        if (_playerView is null)
        {
            return;
        }

        var oldView = _playerView;
        var layer = PlayerSurfaceLayer;
        var insertIndex = layer.Children.IndexOf(oldView);
        if (insertIndex < 0)
        {
            insertIndex = 0;
        }

        _playerContextSubscription?.Dispose();
        _playerContextSubscription = null;
        oldView.ViewInitialized -= OnPlayerViewInitialized;
        oldView.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPlayerSurfacePointerWheelChanged);
        layer.Children.Remove(oldView);

        var nextView = CreateMpvView("PlayerView", OnPlayerViewInitialized);
        _playerContextSubscription = nextView.GetObservable(MpvView.MpvContextProperty)
            .Subscribe(_ => QueuePlayerRendererInitialization());

        layer.Children.Insert(insertIndex, nextView);
        _playerView = nextView;
    }
#endif

    private void OnDetachedPlayerWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isDockingDetachedPlayer)
        {
            return;
        }

        e.Cancel = true;
        Dispatcher.UIThread.Post(DockPlayerToMainWindow);
    }

    private void OnDetachedPlayerWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_detachedPlayerWindow?.WindowState == WindowState.FullScreen && e.Key == Key.Escape)
        {
            _detachedPlayerWindow.WindowState = _detachedPlayerWindowStateBeforeFullscreen == WindowState.FullScreen
                ? WindowState.Normal
                : _detachedPlayerWindowStateBeforeFullscreen;
            e.Handled = true;
            return;
        }

        OnWindowKeyDown(sender, e);
    }

    private void OnDetachedPlayerWindowKeyUp(object? sender, KeyEventArgs e)
    {
        OnWindowKeyUp(sender, e);
    }

    private void UpdatePlayerDetachButtonState()
    {
        var isDetached = _detachedPlayerWindow is not null;
        PlayerDetachButton.Content = isDetached ? "↙" : "↗";
        ToolTip.SetTip(
            PlayerDetachButton,
            isDetached ? "Вернуть плеер в основное окно" : "Открыть плеер в отдельном окне");
    }

    private void MinimizePanel(string panel)
    {
        if (_isPlayerFullscreen)
        {
            _isPlayerFullscreen = false;
            ApplyPlayerFullscreenState();
        }

        if (_maximizedPanel is not null)
        {
            _maximizedPanel = null;
        }

        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
    }

    private void TogglePanelMaximized(string panel)
    {
        if (_isPlayerFullscreen)
        {
            _isPlayerFullscreen = false;
            ApplyPlayerFullscreenState();
        }

        if (string.Equals(_maximizedPanel, panel, StringComparison.Ordinal))
        {
            _maximizedPanel = null;
        }
        else
        {
            EnsurePanelVisible(panel);
            _maximizedPanel = panel;
        }

        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
    }

    private void ClosePanel(string panel)
    {
        if (string.Equals(panel, PanelPlayer, StringComparison.Ordinal)
            && _detachedPlayerWindow is not null)
        {
            DockPlayerToMainWindow();
        }

        _maximizedPanel = string.Equals(_maximizedPanel, panel, StringComparison.Ordinal)
            ? null
            : _maximizedPanel;

        if (TryGetPanelEntity(panel, out var entity))
        {
            _entitySlots.Remove(entity);
        }

        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
    }

    private void EnsurePanelVisible(string panel)
    {
        if (TryGetPanelEntity(panel, out var entity) && !IsEntityVisibleInLayout(entity))
        {
            AssignEntityToSlot(entity, FindFirstAvailableActiveSlot());
        }
    }

    private void OnWorkspaceLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<WorkspaceLayoutKind>(tag, out var layout))
        {
            return;
        }

        _workspaceLayoutKind = layout;
        NormalizeEntitySlotsForLayout();
        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
    }

    private void AddWorkspaceWindow()
    {
        var activeSlotCount = GetActiveSlotBounds().Length;
        var maxSlotCount = GetSlotPlaceholders().Length;
        if (activeSlotCount >= maxSlotCount)
        {
            if (_viewModel is not null)
            {
                _viewModel.StatusMessage = "Доступно максимум 5 окон.";
            }

            return;
        }

        var beforeRects = CaptureWorkspacePanelRects(_entitySlots.Keys.ToArray());
        _workspaceLayoutKind = GetLayoutKindForSlotCount(activeSlotCount + 1);
        NormalizeEntitySlotsForLayout();
        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
        AnimateWorkspacePanelPlacement(beforeRects);

        if (_viewModel is not null)
        {
            _viewModel.StatusMessage = "Окно добавлено.";
        }
    }

    private void OnWorkspaceSplitterPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is GridSplitter splitter && TryGetWorkspaceAddButtonForSplitter(splitter, out var button))
        {
            ShowWorkspaceAddButton(button);
        }
    }

    private void OnWorkspaceSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is GridSplitter splitter && TryGetWorkspaceAddButtonForSplitter(splitter, out var button))
        {
            HideWorkspaceAddButtonSoon(button);
        }
    }

    private void OnWorkspaceAddButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button button)
        {
            _hoveredWorkspaceAddButton = button;
            ShowWorkspaceAddButton(button);
        }
    }

    private void OnWorkspaceAddButtonPointerExited(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(_hoveredWorkspaceAddButton, sender))
        {
            _hoveredWorkspaceAddButton = null;
        }

        if (sender is Button button)
        {
            HideWorkspaceAddButtonSoon(button);
        }
    }

    private void OnWorkspaceEdgeAddWindowClick(object? sender, RoutedEventArgs e)
    {
        AddWorkspaceWindow();
        HideWorkspaceAddButtons();
    }

    private bool TryGetWorkspaceAddButtonForSplitter(GridSplitter splitter, out Button button)
    {
        if (ReferenceEquals(splitter, EventsPanelSplitter))
        {
            button = EventsSplitterAddWindowButton;
            return true;
        }

        if (ReferenceEquals(splitter, AnalysisPanelSplitter))
        {
            button = AnalysisSplitterAddWindowButton;
            return true;
        }

        if (ReferenceEquals(splitter, TimelinePanelSplitter))
        {
            button = TimelineSplitterAddWindowButton;
            return true;
        }

        button = null!;
        return false;
    }

    private void ShowWorkspaceAddButton(Button button)
    {
        if (GetActiveSlotBounds().Length >= GetSlotPlaceholders().Length)
        {
            HideWorkspaceAddButtons();
            return;
        }

        if (_visibleWorkspaceAddButton is { } visibleButton && !ReferenceEquals(visibleButton, button))
        {
            visibleButton.IsVisible = false;
        }

        _visibleWorkspaceAddButton = button;
        button.IsVisible = true;
    }

    private async void HideWorkspaceAddButtonSoon(Button button)
    {
        await Task.Delay(120);
        if (ReferenceEquals(_hoveredWorkspaceAddButton, button))
        {
            return;
        }

        if (ReferenceEquals(_visibleWorkspaceAddButton, button))
        {
            button.IsVisible = false;
            _visibleWorkspaceAddButton = null;
        }
    }

    private void HideWorkspaceAddButtons()
    {
        EventsSplitterAddWindowButton.IsVisible = false;
        AnalysisSplitterAddWindowButton.IsVisible = false;
        TimelineSplitterAddWindowButton.IsVisible = false;
        _visibleWorkspaceAddButton = null;
        _hoveredWorkspaceAddButton = null;
    }

    private void OnPanelHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null
            || _isPlayerFullscreen
            || _maximizedPanel is not null
            || sender is not Control { Tag: string panel }
            || !TryGetPanelEntity(panel, out var entity)
            || !IsEntityVisibleInLayout(entity))
        {
            return;
        }

        if (HasVisualAncestor<Button>(e.Source)
            || HasVisualAncestor<ToggleButton>(e.Source)
            || HasVisualAncestor<ComboBox>(e.Source)
            || HasVisualAncestor(TimelineZoomSliderRoot, e.Source))
        {
            return;
        }

        var point = e.GetCurrentPoint((Control)sender);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!_entitySlots.TryGetValue(entity, out var sourceSlot))
        {
            return;
        }

        _draggedWorkspaceEntity = entity;
        _workspacePanelDragSourceSlot = sourceSlot;
        _workspacePanelDragStartPoint = e.GetPosition(MainLayoutGrid);
        _isWorkspacePanelDragging = false;
        e.Pointer.Capture(MainLayoutGrid);
        e.Handled = true;
    }

    private void OnWorkspacePanelDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedWorkspaceEntity is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(MainLayoutGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(null);
            ClearWorkspacePanelDrag();
            return;
        }

        var currentPoint = e.GetPosition(MainLayoutGrid);
        var offset = currentPoint - _workspacePanelDragStartPoint;
        var distanceSquared = (offset.X * offset.X) + (offset.Y * offset.Y);

        if (!_isWorkspacePanelDragging
            && distanceSquared >= WorkspacePanelDragThreshold * WorkspacePanelDragThreshold)
        {
            _isWorkspacePanelDragging = true;
            BeginWorkspacePanelDragVisual(_draggedWorkspaceEntity.Value);
            if (_viewModel is not null)
            {
                _viewModel.StatusMessage = "Отпустите окно над нужным слотом, чтобы изменить расположение.";
            }
        }

        if (_isWorkspacePanelDragging)
        {
            UpdateWorkspacePanelDropHighlight(currentPoint);
            e.Handled = true;
        }
    }

    private void OnWorkspacePanelDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedWorkspaceEntity is not { } entity)
        {
            return;
        }

        var wasDragging = _isWorkspacePanelDragging;
        var dropPoint = e.GetPosition(MainLayoutGrid);

        e.Pointer.Capture(null);
        ClearWorkspacePanelDrag();

        if (!wasDragging)
        {
            return;
        }

        if (TryGetWorkspaceSlotAtPoint(dropPoint, out var targetSlot))
        {
            MoveWorkspaceEntityToSlot(entity, targetSlot);
        }

        e.Handled = true;
    }

    private void ClearWorkspacePanelDrag()
    {
        ResetWorkspacePanelDragVisual();
        ClearWorkspacePanelDropHighlight();
        _draggedWorkspaceEntity = null;
        _workspacePanelDragSourceSlot = -1;
        _isWorkspacePanelDragging = false;
    }

    private void BeginWorkspacePanelDragVisual(WorkspaceEntityKind entity)
    {
        ResetWorkspacePanelDragVisual();

        var panel = GetWorkspaceEntityPanel(entity);
        _workspacePanelDragVisualEntity = entity;
        panel.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        panel.RenderTransform = new ScaleTransform(0.992, 0.992);
        panel.Opacity = 0.78;
    }

    private void ResetWorkspacePanelDragVisual()
    {
        if (_workspacePanelDragVisualEntity is not { } entity)
        {
            return;
        }

        var panel = GetWorkspaceEntityPanel(entity);
        panel.RenderTransform = null;
        panel.Opacity = 1d;
        _workspacePanelDragVisualEntity = null;
    }

    private void UpdateWorkspacePanelDropHighlight(Point point)
    {
        if (!_isWorkspacePanelDragging
            || !TryGetWorkspaceSlotAtPoint(point, out var targetSlot)
            || targetSlot == _workspacePanelDragSourceSlot)
        {
            ClearWorkspacePanelDropHighlight();
            return;
        }

        HighlightWorkspacePanelDropTarget(targetSlot);
    }

    private void HighlightWorkspacePanelDropTarget(int targetSlot)
    {
        if (_workspacePanelDropTargetSlot == targetSlot)
        {
            return;
        }

        ClearWorkspacePanelDropHighlight();

        if (GetWorkspaceSlotControl(targetSlot) is not Border border)
        {
            return;
        }

        _workspacePanelDropTargetSlot = targetSlot;
        _workspacePanelDropTargetBorder = border;
        border.BorderBrush = new SolidColorBrush(Color.Parse("#4FA0FF"));
        border.BorderThickness = new Thickness(2);
    }

    private void ClearWorkspacePanelDropHighlight()
    {
        if (_workspacePanelDropTargetBorder is { } border)
        {
            border.ClearValue(Border.BorderBrushProperty);
            border.ClearValue(Border.BorderThicknessProperty);
        }

        _workspacePanelDropTargetBorder = null;
        _workspacePanelDropTargetSlot = -1;
    }

    private void OnCreateSlotEntityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var slotIndex)
            || !Enum.TryParse<WorkspaceEntityKind>(parts[1], out var entity))
        {
            return;
        }

        if (entity == WorkspaceEntityKind.Broadcast && _viewModel?.IsBroadcastModeProject != true)
        {
            return;
        }

        AssignEntityToSlot(entity, slotIndex);
        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
    }

    private void OnCloseEmptySlotClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var slotIndex))
        {
            return;
        }

        RemoveEmptyWorkspaceSlot(slotIndex);
    }

    private void OnToggleFullscreenClick(object? sender, RoutedEventArgs e)
    {
        if (_detachedPlayerWindow is not null)
        {
            if (_detachedPlayerWindow.WindowState == WindowState.FullScreen)
            {
                _detachedPlayerWindow.WindowState = _detachedPlayerWindowStateBeforeFullscreen == WindowState.FullScreen
                    ? WindowState.Normal
                    : _detachedPlayerWindowStateBeforeFullscreen;
            }
            else
            {
                _detachedPlayerWindowStateBeforeFullscreen = _detachedPlayerWindow.WindowState;
                _detachedPlayerWindow.WindowState = WindowState.FullScreen;
            }

            return;
        }

        _isPlayerFullscreen = !_isPlayerFullscreen;
        ApplyPlayerFullscreenState();
        UpdatePanelLayout();
    }

    private void OnPlayerSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint((Control?)sender ?? PlayerSurfaceHost);
        if (_viewModel.IsPresetEditorOpen
            || _viewModel.IsTagEventEditorOpen
            || _viewModel.IsExportDialogOpen
            || _viewModel.IsNewProjectDialogOpen
            || _viewModel.IsStartupScreenVisible)
        {
            return;
        }

        if (TryGetNormalizedVideoPoint(point.Position, out var normalizedPoint))
        {
            _lastVideoZoomFocus = normalizedPoint;
        }

        if ((point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
            && _viewModel.VideoZoom > 1.001d)
        {
            _isVideoPanDragging = true;
            _lastVideoPanPoint = point.Position;
            if (sender is IInputElement inputElement)
            {
                e.Pointer.Capture(inputElement);
            }

            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _viewModel.TogglePlayPauseCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPlayerSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var point = e.GetPosition(PlayerSurfaceHost);
        if (_isVideoPanDragging)
        {
            var displayRect = GetDisplayedVideoRect();
            var displayWidth = displayRect.Width;
            var displayHeight = displayRect.Height;
            var deltaX = displayWidth <= 0
                ? 0d
                : ((point.X - _lastVideoPanPoint.X) / displayWidth) * _viewModel.VideoZoom;
            var deltaY = displayHeight <= 0
                ? 0d
                : ((point.Y - _lastVideoPanPoint.Y) / displayHeight) * _viewModel.VideoZoom;
            _viewModel.PanVideoZoom(deltaX, deltaY);
            _lastVideoPanPoint = point;
            e.Handled = true;
            return;
        }

        if (TryGetNormalizedVideoPoint(point, out var normalizedPoint))
        {
            _lastVideoZoomFocus = normalizedPoint;
        }
    }

    private void OnPlayerSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isVideoPanDragging)
        {
            return;
        }

        _isVideoPanDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPlayerSurfacePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        HandlePlayerZoomWheel(e);
    }

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        HandlePlayerZoomWheel(e);
    }

    private void HandlePlayerZoomWheel(PointerWheelEventArgs e)
    {
        var point = e.GetPosition(PlayerSurfaceHost);
        if (TryZoomVideoFromWheel(e.Delta.Y, point, PlayerSurfaceHost.PointToScreen(point)))
        {
            e.Handled = true;
        }
    }

    private bool TryZoomVideoFromWheel(double delta, Point point, PixelPoint screenPoint)
    {
        if (_viewModel is null
            || Math.Abs(delta) < 0.001d
            || !_viewModel.IsPlayerSurfaceVisible
            || _isPlayerPanelHidden
            || point.X < 0
            || point.Y < 0
            || point.X > PlayerSurfaceHost.Bounds.Width
            || point.Y > PlayerSurfaceHost.Bounds.Height)
        {
            return false;
        }

        if (IsDuplicateVideoWheel(delta, screenPoint))
        {
            return true;
        }

        if (TryGetNormalizedVideoPoint(point, out var normalizedPoint))
        {
            _lastVideoZoomFocus = normalizedPoint;
        }

        _viewModel.ZoomVideo(delta, _lastVideoZoomFocus.X, _lastVideoZoomFocus.Y);
        return true;
    }

    private bool IsDuplicateVideoWheel(double delta, PixelPoint screenPoint)
    {
        var now = Environment.TickCount64;
        var isDuplicate = now - _lastVideoWheelTick <= VideoWheelDuplicateWindowMs
            && Math.Sign(delta) == Math.Sign(_lastVideoWheelDelta)
            && Math.Abs(screenPoint.X - _lastVideoWheelScreenPoint.X) <= 2
            && Math.Abs(screenPoint.Y - _lastVideoWheelScreenPoint.Y) <= 2;

        _lastVideoWheelTick = now;
        _lastVideoWheelDelta = delta;
        _lastVideoWheelScreenPoint = screenPoint;
        return isDuplicate;
    }

    private bool TryHandleNativePlayerZoomWheel(double delta, PixelPoint screenPoint)
    {
        if (!TryGetPlayerSurfacePoint(screenPoint, out var point))
        {
            return false;
        }

        return TryZoomVideoFromWheel(delta, point, screenPoint);
    }

    private void HandleNativePlayerPinch(double zoomLevel, Point point)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (TryGetNormalizedVideoPoint(point, out var normalizedPoint))
        {
            _lastVideoZoomFocus = normalizedPoint;
        }

        _viewModel.SetVideoZoomLevel(zoomLevel, _lastVideoZoomFocus.X, _lastVideoZoomFocus.Y);
    }

    private bool TryHandleNativePlayerPinch(double zoomLevel, PixelPoint screenPoint)
    {
        if (!TryGetPlayerSurfacePoint(screenPoint, out var point))
        {
            return false;
        }

        Dispatcher.UIThread.Post(() => HandleNativePlayerPinch(zoomLevel, point));
        return true;
    }

    private bool TryGetPlayerSurfacePoint(PixelPoint screenPoint, out Point point)
    {
        point = default;
        if (_viewModel is null
            || !_viewModel.IsPlayerSurfaceVisible
            || _isPlayerPanelHidden
            || PlayerSurfaceHost.Bounds.Width <= 0
            || PlayerSurfaceHost.Bounds.Height <= 0)
        {
            return false;
        }

        point = PlayerSurfaceHost.PointToClient(screenPoint);
        return point.X >= 0
            && point.Y >= 0
            && point.X <= PlayerSurfaceHost.Bounds.Width
            && point.Y <= PlayerSurfaceHost.Bounds.Height;
    }

    private void TryHookWindowInput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || TryGetPlatformHandle() is not { Handle: var handle }
            || handle == IntPtr.Zero)
        {
            return;
        }

        _mainWindowHandle = handle;
        if (_windowInputHookHandle != IntPtr.Zero)
        {
            return;
        }

        _windowInputProc = OnWindowNativeMessage;
        _previousWindowInputProc = SetWindowLongPtr(
            handle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowInputProc));

        if (_previousWindowInputProc == IntPtr.Zero)
        {
            _windowInputProc = null;
            return;
        }

        _windowInputHookHandle = handle;
        EnableZoomGesture(handle);
    }

    private void UnhookWindowInput()
    {
        if (_windowInputHookHandle != IntPtr.Zero && _previousWindowInputProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowInputHookHandle, GwlWndProc, _previousWindowInputProc);
        }

        _windowInputHookHandle = IntPtr.Zero;
        _previousWindowInputProc = IntPtr.Zero;
        _windowInputProc = null;
    }

    private void TryHookVideoWheel(IntPtr handle)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || handle == IntPtr.Zero
            || _videoWheelHookHandle == handle)
        {
            return;
        }

        UnhookVideoWheel();
        _videoWindowProc = OnVideoNativeWindowMessage;
        _previousVideoWindowProc = SetWindowLongPtr(
            handle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_videoWindowProc));

        if (_previousVideoWindowProc == IntPtr.Zero)
        {
            _videoWindowProc = null;
            return;
        }

        _videoWheelHookHandle = handle;
        EnableZoomGesture(handle);
        UpdateVideoZoomLayout();
    }

    private void UnhookVideoWheel()
    {
        if (_videoWheelHookHandle != IntPtr.Zero && _previousVideoWindowProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_videoWheelHookHandle, GwlWndProc, _previousVideoWindowProc);
        }

        _videoWheelHookHandle = IntPtr.Zero;
        _previousVideoWindowProc = IntPtr.Zero;
        _videoWindowProc = null;
    }

    private IntPtr OnVideoNativeWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (TryHandleNativeZoomMessage(message, wParam, lParam, out var result))
        {
            return result;
        }

        return _previousVideoWindowProc == IntPtr.Zero
            ? IntPtr.Zero
            : CallWindowProc(_previousVideoWindowProc, hWnd, message, wParam, lParam);
    }

    private IntPtr OnWindowNativeMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (TryHandleNativeZoomMessage(message, wParam, lParam, out var result))
        {
            return result;
        }

        return _previousWindowInputProc == IntPtr.Zero
            ? IntPtr.Zero
            : CallWindowProc(_previousWindowInputProc, hWnd, message, wParam, lParam);
    }

    private bool TryHandleNativeZoomMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == WmMouseWheel)
        {
            var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            var point = GetPointFromLParam(lParam);
            return TryHandleNativePlayerZoomWheel(delta, new PixelPoint(point.X, point.Y));
        }

        if (message == WmGesture && TryHandleNativeGesture(lParam))
        {
            return true;
        }

        return false;
    }

    private bool TryHandleNativeGesture(IntPtr gestureHandle)
    {
        var info = new GestureInfo { Size = (uint)Marshal.SizeOf<GestureInfo>() };
        if (!GetGestureInfo(gestureHandle, ref info))
        {
            return false;
        }

        if (info.Id != GidZoom)
        {
            return false;
        }

        var point = new PixelPoint(info.LocationX, info.LocationY);
        var isBeginning = (info.Flags & GfBegin) != 0 || _gestureZoomStartDistance == 0;
        if (isBeginning && !TryGetPlayerSurfacePoint(point, out _))
        {
            return false;
        }

        try
        {
            if ((info.Flags & GfEnd) != 0)
            {
                _gestureZoomStartDistance = 0;
                return true;
            }

            if (info.Arguments == 0)
            {
                return true;
            }

            if (isBeginning)
            {
                _gestureZoomStartDistance = info.Arguments;
                _gestureZoomStartLevel = _viewModel?.VideoZoom ?? 1d;
                TryHandleNativePlayerPinch(_gestureZoomStartLevel, point);
                return true;
            }

            var factor = info.Arguments / (double)_gestureZoomStartDistance;
            var nextZoom = _gestureZoomStartLevel * factor;
            TryHandleNativePlayerPinch(nextZoom, point);
            return true;
        }
        finally
        {
            CloseGestureInfoHandle(gestureHandle);
        }
    }

    private static NativePoint GetPointFromLParam(IntPtr lParam)
    {
        return new NativePoint
        {
            X = unchecked((short)(lParam.ToInt64() & 0xFFFF)),
            Y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF))
        };
    }

    private static void EnableZoomGesture(IntPtr handle)
    {
        var configs = new[]
        {
            new GestureConfig
            {
                Id = GidZoom,
                Want = GcZoom,
                Block = 0
            }
        };

        SetGestureConfig(handle, 0, (uint)configs.Length, configs, (uint)Marshal.SizeOf<GestureConfig>());
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
        {
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        }

        return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    private void OnZoomVideoInClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ZoomVideo(1d, _lastVideoZoomFocus.X, _lastVideoZoomFocus.Y);
    }

    private void OnZoomVideoOutClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ZoomVideo(-1d, _lastVideoZoomFocus.X, _lastVideoZoomFocus.Y);
    }

    private void OnResetVideoZoomClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ResetVideoZoomCommand.Execute(null);
        _lastVideoZoomFocus = new Point(0.5d, 0.5d);
    }

    private bool TryGetNormalizedVideoPoint(Point point, out Point normalizedPoint)
    {
        var displayRect = GetDisplayedVideoRect();
        if (displayRect.Width <= 0 || displayRect.Height <= 0)
        {
            normalizedPoint = _lastVideoZoomFocus;
            return false;
        }

        normalizedPoint = new Point(
            Math.Clamp((point.X - displayRect.X) / displayRect.Width, 0d, 1d),
            Math.Clamp((point.Y - displayRect.Y) / displayRect.Height, 0d, 1d));
        return true;
    }

    private void UpdateVideoZoomLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        var hostWidth = PlayerSurfaceHost.Bounds.Width;
        var hostHeight = PlayerSurfaceHost.Bounds.Height;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return;
        }

        var scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1d;
        _viewModel.SetVideoViewportSize(hostWidth * scale, hostHeight * scale);

        var displayRect = GetDisplayedVideoRect();
        if (displayRect.Width <= 0 || displayRect.Height <= 0)
        {
            return;
        }

#if WINDOWS_MPV
        if (_playerView is not null)
        {
            _playerView.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _playerView.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            _playerView.Width = double.NaN;
            _playerView.Height = double.NaN;
            _playerView.Margin = default;
        }
#endif

        if (_playerLibVlcView is not null)
        {
            _playerLibVlcView.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _playerLibVlcView.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            _playerLibVlcView.Width = double.NaN;
            _playerLibVlcView.Height = double.NaN;
            _playerLibVlcView.Margin = default;
        }

        UpdateVideoZoomDiagnostics(displayRect);
    }

    private void ApplyVideoWindowBounds(Rect displayRect)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || _videoWheelHookHandle == IntPtr.Zero)
        {
            return;
        }

        var parentHandle = GetParent(_videoWheelHookHandle);
        if (parentHandle == IntPtr.Zero)
        {
            return;
        }

        var scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1d;
        var hostScreenPoint = PlayerSurfaceHost.PointToScreen(new Point(0, 0));
        var hostParentPoint = new NativePoint { X = hostScreenPoint.X, Y = hostScreenPoint.Y };
        if (!ScreenToClient(parentHandle, ref hostParentPoint))
        {
            return;
        }

        var x = hostParentPoint.X + (int)Math.Round(displayRect.X * scale);
        var y = hostParentPoint.Y + (int)Math.Round(displayRect.Y * scale);
        var width = Math.Max(1, (int)Math.Round(displayRect.Width * scale));
        var height = Math.Max(1, (int)Math.Round(displayRect.Height * scale));

        SetWindowPos(
            _videoWheelHookHandle,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void ApplyVideoWindowClip(Rect displayRect)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || _videoWheelHookHandle == IntPtr.Zero
            || PlayerSurfaceHost.Bounds.Width <= 0
            || PlayerSurfaceHost.Bounds.Height <= 0)
        {
            return;
        }

        var scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1d;
        var left = (int)Math.Floor(Math.Max(0d, -displayRect.X) * scale);
        var top = (int)Math.Floor(Math.Max(0d, -displayRect.Y) * scale);
        var right = (int)Math.Ceiling(Math.Min(displayRect.Width, PlayerSurfaceHost.Bounds.Width - displayRect.X) * scale);
        var bottom = (int)Math.Ceiling(Math.Min(displayRect.Height, PlayerSurfaceHost.Bounds.Height - displayRect.Y) * scale);

        right = Math.Max(left + 1, right);
        bottom = Math.Max(top + 1, bottom);

        var fullWidth = (int)Math.Ceiling(displayRect.Width * scale);
        var fullHeight = (int)Math.Ceiling(displayRect.Height * scale);
        if (left <= 0 && top <= 0 && right >= fullWidth && bottom >= fullHeight)
        {
            SetWindowRgn(_videoWheelHookHandle, IntPtr.Zero, true);
            return;
        }

        var region = CreateRectRgn(left, top, right, bottom);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(_videoWheelHookHandle, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    private Rect GetDisplayedVideoRect()
    {
        var hostWidth = PlayerSurfaceHost.Bounds.Width;
        var hostHeight = PlayerSurfaceHost.Bounds.Height;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return default;
        }

        var zoom = Math.Max(1d, _viewModel?.VideoZoom ?? 1d);
        var (baseWidth, baseHeight) = GetDisplayedVideoSize(coverHost: zoom > 1.001d);
        if (baseWidth <= 0 || baseHeight <= 0)
        {
            return default;
        }

        var width = baseWidth * zoom;
        var height = baseHeight * zoom;
        var centerX = Math.Clamp(_viewModel?.VideoZoomCenterX ?? 0.5d, 0d, 1d);
        var centerY = Math.Clamp(_viewModel?.VideoZoomCenterY ?? 0.5d, 0d, 1d);
        var left = (hostWidth / 2d) - (width * centerX);
        var top = (hostHeight / 2d) - (height * centerY);

        if (width <= hostWidth)
        {
            left = (hostWidth - width) / 2d;
        }
        else
        {
            left = Math.Clamp(left, hostWidth - width, 0d);
        }

        if (height <= hostHeight)
        {
            top = (hostHeight - height) / 2d;
        }
        else
        {
            top = Math.Clamp(top, hostHeight - height, 0d);
        }

        return new Rect(left, top, width, height);
    }

    private (double Width, double Height) GetDisplayedVideoSize(bool coverHost = false)
    {
        var hostWidth = PlayerSurfaceHost.Bounds.Width;
        var hostHeight = PlayerSurfaceHost.Bounds.Height;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return (0d, 0d);
        }

        var videoWidth = _viewModel?.VideoWidth ?? 0;
        var videoHeight = _viewModel?.VideoHeight ?? 0;
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return (hostWidth, hostHeight);
        }

        var videoAspect = videoWidth / (double)videoHeight;
        var hostAspect = hostWidth / hostHeight;
        var fitHeight = coverHost
            ? hostAspect < videoAspect
            : hostAspect > videoAspect;
        if (fitHeight)
        {
            return (hostHeight * videoAspect, hostHeight);
        }

        return (hostWidth, hostWidth / videoAspect);
    }

    private void ToggleVideoZoomDiagnostics()
    {
        _isVideoZoomDiagnosticsVisible = !_isVideoZoomDiagnosticsVisible;
        VideoZoomDiagnosticsOverlay.IsVisible = _isVideoZoomDiagnosticsVisible;
        UpdateVideoZoomLayout();
    }

    private void UpdateVideoZoomDiagnostics(Rect displayRect)
    {
        VideoZoomDiagnosticsOverlay.IsVisible = _isVideoZoomDiagnosticsVisible;
        if (!_isVideoZoomDiagnosticsVisible || _viewModel is null)
        {
            return;
        }

        var scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1d;
        var nativeText = "n/a";
        if (_videoWheelHookHandle != IntPtr.Zero && GetWindowRect(_videoWheelHookHandle, out var nativeRect))
        {
            nativeText = $"{nativeRect.Right - nativeRect.Left:0}x{nativeRect.Bottom - nativeRect.Top:0}";
        }

        VideoZoomDiagnosticsText.Text =
            $"zoom: {_viewModel.VideoZoom:0.##}x  center: {_viewModel.VideoZoomCenterX:0.###};{_viewModel.VideoZoomCenterY:0.###}\n" +
            $"video: {_viewModel.VideoWidth}x{_viewModel.VideoHeight}\n" +
            $"host: {PlayerSurfaceHost.Bounds.Width:0.#}x{PlayerSurfaceHost.Bounds.Height:0.#} @ {scale:0.##}x\n" +
            $"rect: {displayRect.X:0.#};{displayRect.Y:0.#} {displayRect.Width:0.#}x{displayRect.Height:0.#}\n" +
            $"hwnd: {nativeText}";
    }

    private void OnPlaybackRateMenuActionClick(object? sender, RoutedEventArgs e)
    {
        SpeedMenuButton.IsChecked = false;
    }

    private void OnEventTypeItemDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: EventTypeItemViewModel eventTypeItem })
        {
            return;
        }

        _viewModel.OpenPresetEditor(eventTypeItem.Preset);
    }

    private void OnTagEventItemDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: VideoAnalysis.App.ViewModels.Items.TagEventItemViewModel tagEvent })
        {
            return;
        }

        _viewModel.OpenTagEventEditor(tagEvent);
    }

    private void OnTagEventPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: VideoAnalysis.App.ViewModels.Items.TagEventItemViewModel tagEvent })
        {
            return;
        }

        _viewModel.SeekToTagEventStart(tagEvent);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_isPlayerFullscreen && e.Key == Key.Escape)
        {
            _isPlayerFullscreen = false;
            ApplyPlayerFullscreenState();
            UpdatePanelLayout();
            e.Handled = true;
            return;
        }

        if (_viewModel.IsNewProjectDialogOpen
            || _viewModel.IsStartupScreenVisible
            || _viewModel.IsExportDialogOpen
            || _viewModel.IsPresetEditorOpen)
        {
            return;
        }

        if (ShouldIgnoreHotkeys(e.Source))
        {
            return;
        }

        if (TryHandleVideoZoomHotkey(e))
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Space)
        {
            _viewModel.TogglePlayPauseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Left)
        {
            _viewModel.SeekBackwardOneSecondCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Right)
        {
            _viewModel.SeekForwardOneSecondCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var hotkey = TryMapHotkey(e);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return;
        }

        await _viewModel.HandleEventTypeHotkeyAsync(hotkey);
        e.Handled = true;
    }

    private bool TryHandleVideoZoomHotkey(KeyEventArgs e)
    {
        if (_viewModel is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return false;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.D)
        {
            ToggleVideoZoomDiagnostics();
            return true;
        }

        if (e.Key is Key.Add or Key.OemPlus)
        {
            _viewModel.ZoomVideo(1d, _lastVideoZoomFocus.X, _lastVideoZoomFocus.Y);
            return true;
        }

        if (e.Key is Key.Subtract or Key.OemMinus)
        {
            _viewModel.ZoomVideo(-1d, _lastVideoZoomFocus.X, _lastVideoZoomFocus.Y);
            return true;
        }

        if (e.Key is Key.D0 or Key.NumPad0)
        {
            _viewModel.ResetVideoZoomCommand.Execute(null);
            _lastVideoZoomFocus = new Point(0.5d, 0.5d);
            return true;
        }

        return false;
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (ShouldIgnoreHotkeys(e.Source))
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Space)
        {
            e.Handled = true;
        }
    }

    private void OnEventTypeHotkeyTextInput(object? sender, TextInputEventArgs e)
    {
        if (_viewModel is null || sender is not TextBox textBox)
        {
            return;
        }

        var replacement = TryExtractSingleEnglishLetter(e.Text);
        e.Handled = true;

        if (replacement is null)
        {
            return;
        }

        _viewModel.EventTypeHotkey = replacement;
        textBox.Text = _viewModel.EventTypeHotkey;
        textBox.CaretIndex = textBox.Text?.Length ?? 0;
    }

    private void OnEventTypeHotkeyEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            _viewModel.EventTypeHotkey = string.Empty;
            textBox.Text = string.Empty;
            textBox.CaretIndex = 0;
            e.Handled = true;
        }
    }

    private void OnEventTypeHotkeyTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isAdjustingEventTypeHotkeyText || _viewModel is null || sender is not TextBox textBox)
        {
            return;
        }

        var normalized = TryExtractSingleEnglishLetter(textBox.Text) ?? string.Empty;
        _viewModel.EventTypeHotkey = normalized;
        var finalText = _viewModel.EventTypeHotkey ?? string.Empty;

        if (string.Equals(textBox.Text, finalText, StringComparison.Ordinal))
        {
            return;
        }

        _isAdjustingEventTypeHotkeyText = true;
        textBox.Text = finalText;
        textBox.CaretIndex = finalText.Length;
        _isAdjustingEventTypeHotkeyText = false;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!_viewModel.IsPresetEditorOpen
            && !_viewModel.IsTagEventEditorOpen
            && !_viewModel.IsExportDialogOpen
            && !_viewModel.IsNewProjectDialogOpen
            && !_viewModel.IsStartupScreenVisible)
        {
            return;
        }

        if (HasVisualAncestor<TextBox>(e.Source))
        {
            return;
        }

        if (HasVisualAncestor<Button>(e.Source)
            || HasVisualAncestor<ToggleButton>(e.Source)
            || HasVisualAncestor<ComboBox>(e.Source)
            || HasVisualAncestor<ListBoxItem>(e.Source))
        {
            return;
        }

        if (e.Source is Control { Focusable: true })
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.IsPresetEditorOpen)
            {
                PresetEditorDialog.Focus();
            }
            else if (_viewModel.IsTagEventEditorOpen)
            {
                TagEventEditorDialog.Focus();
            }
            else if (_viewModel.IsExportDialogOpen)
            {
                ExportDialogCloseButton.Focus();
            }
        });
    }

    private void OnSeekBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateSeekBarVisuals();
    }

    private void OnPlayerSurfaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateVideoZoomLayout();
    }

    private void OnBroadcastSurfaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1
            || e.NewSize.Height <= 1
            || e.PreviousSize.Width <= 1
            || e.PreviousSize.Height <= 1)
        {
            return;
        }

        QueueBroadcastLivePreviewRecovery(forceRefresh: true);
    }

    private void OnTimelineViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _viewModel?.SetTimelineViewport(e.NewSize.Width, TimelineHorizontalScrollViewer.Offset.X);
        Dispatcher.UIThread.Post(() => EnsureTimelineCurrentFrameVisible(force: true), DispatcherPriority.Loaded);
    }

    private void OnTimelineHorizontalScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _viewModel?.SetTimelineViewport(
            TimelineHorizontalScrollViewer.Viewport.Width,
            TimelineHorizontalScrollViewer.Offset.X,
            refreshContent: !_isTimelinePanDragging);
    }

    private void OnTimelinePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
    }

    private void OnTimelinePanPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineHorizontalScrollViewer);
        if (!point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        e.Handled = true;
    }

    private void OnTimelinePanPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTimelinePanDragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(TimelineHorizontalScrollViewer);
        if (!point.Properties.IsMiddleButtonPressed)
        {
            FinishTimelinePanDrag(e.Pointer);
            return;
        }

        e.Handled = true;
    }

    private void OnTimelinePanPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isTimelinePanDragging)
        {
            return;
        }

        FinishTimelinePanDrag(e.Pointer);
        e.Handled = true;
    }

    private void FinishTimelinePanDrag(IPointer pointer)
    {
        _isTimelinePanDragging = false;
        pointer.Capture(null);
        _viewModel?.SetTimelineViewport(
            TimelineHorizontalScrollViewer.Viewport.Width,
            TimelineHorizontalScrollViewer.Offset.X,
            refreshContent: true);
        QueueBroadcastLivePreviewRecovery(forceRefresh: true);
    }

    private void OnTimelineSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(TimelineCanvasRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var timelineX = GetTimelinePointerX(e);
        if (Math.Abs(timelineX - _viewModel.TimelineCurrentLineLeft) > TimelineSeekHandleHitWidth)
        {
            SeekTimelineToPointer(e, commit: true);
            e.Handled = true;
            return;
        }

        _isTimelineSeekDragging = true;
        _hasTimelineSeekMoved = false;
        e.Pointer.Capture(TimelineCanvasRoot);
        e.Handled = true;
    }

    private void OnTimelineEventSegmentPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint((Control?)sender ?? TimelineCanvasRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Control { DataContext: TimelineEventSegmentItemViewModel segment })
        {
            _viewModel.CommitTimelineSeekFrame(segment.StartFrame);
            QueueBroadcastLivePreviewRecovery(forceRefresh: true);
            e.Handled = true;
        }
    }

    private void OnTimelineSeekPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTimelineSeekDragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(TimelineCanvasRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishTimelineSeekDrag(e.Pointer);
            return;
        }

        _hasTimelineSeekMoved = true;
        SeekTimelineToPointer(e, commit: false);
        e.Handled = true;
    }

    private void OnTimelineSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isTimelineSeekDragging)
        {
            return;
        }

        if (_hasTimelineSeekMoved)
        {
            SeekTimelineToPointer(e, commit: true);
        }

        FinishTimelineSeekDrag(e.Pointer);
        e.Handled = true;
    }

    private void FinishTimelineSeekDrag(IPointer pointer)
    {
        _isTimelineSeekDragging = false;
        _hasTimelineSeekMoved = false;
        pointer.Capture(null);
    }

    private void SeekTimelineToPointer(PointerEventArgs e, bool commit)
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = Math.Max(1d, _viewModel.TimelineCanvasWidth);
        var timelineX = GetTimelinePointerX(e);
        var ratio = Math.Clamp(timelineX / width, 0d, 1d);
        var targetFrame = (long)Math.Round(ratio * Math.Max(0, _viewModel.TimelineDurationFrames));
        if (commit)
        {
            _viewModel.CommitTimelineSeekFrame(targetFrame);
            QueueBroadcastLivePreviewRecovery(forceRefresh: true);
        }
        else
        {
            _viewModel.PreviewTimelineSeekFrame(targetFrame);
        }
    }

    private double GetTimelinePointerX(PointerEventArgs e)
    {
        var pointerInViewport = e.GetPosition(TimelineHorizontalScrollViewer);
        var timelineX = pointerInViewport.X + TimelineHorizontalScrollViewer.Offset.X;
        return Math.Clamp(timelineX, 0d, Math.Max(1d, _viewModel?.TimelineCanvasWidth ?? 1d));
    }

    private void OnSeekBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(SeekBarRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isSeekDragging = true;
        e.Pointer.Capture((IInputElement?)sender);
        SeekToPointerPosition(e, commit: true);
        e.Handled = true;
    }

    private void OnSeekBarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSeekDragging)
        {
            return;
        }

        SeekToPointerPosition(e, commit: false);
        e.Handled = true;
    }

    private void OnSeekBarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSeekDragging)
        {
            return;
        }

        SeekToPointerPosition(e, commit: true);
        e.Pointer.Capture(null);
        _isSeekDragging = false;
        e.Handled = true;
    }

    private void OnVolumeBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _isVolumeDragging = true;
        e.Pointer.Capture((IInputElement?)sender);
        SetVolumeFromPointer(e);
        e.Handled = true;
    }

    private void OnVolumeBarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isVolumeDragging)
        {
            return;
        }

        SetVolumeFromPointer(e);
        e.Handled = true;
    }

    private void OnVolumeBarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isVolumeDragging)
        {
            return;
        }

        SetVolumeFromPointer(e);
        e.Pointer.Capture(null);
        _isVolumeDragging = false;
        e.Handled = true;
    }

    private void OnTimelineZoomSliderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateTimelineZoomSliderVisuals();
    }

    private void OnTimelineZoomSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(TimelineZoomSliderRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isTimelineZoomSliderDragging = true;
        e.Pointer.Capture((IInputElement?)sender);
        SetTimelineZoomFromPointer(e);
        e.Handled = true;
    }

    private void OnTimelineZoomSliderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTimelineZoomSliderDragging)
        {
            return;
        }

        SetTimelineZoomFromPointer(e);
        e.Handled = true;
    }

    private void OnTimelineZoomSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isTimelineZoomSliderDragging)
        {
            return;
        }

        SetTimelineZoomFromPointer(e);
        e.Pointer.Capture(null);
        _isTimelineZoomSliderDragging = false;
        QueueBroadcastLivePreviewRecovery(forceRefresh: true);
        e.Handled = true;
    }

    private void SeekToPointerPosition(PointerEventArgs e, bool commit)
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = SeekBarRoot.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var point = e.GetPosition(SeekBarRoot);
        var ratio = Math.Clamp(point.X / width, 0d, 1d);
        var targetFrame = (long)Math.Round(ratio * Math.Max(1, _viewModel.DurationFrames));
        if (commit)
        {
            _viewModel.CommitPlayerSeekFrame(targetFrame);
        }
        else
        {
            _viewModel.PreviewPlayerSeekFrame(targetFrame);
        }

        UpdateSeekBarVisuals();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentFrame) or nameof(MainWindowViewModel.DurationFrames))
        {
            UpdateSeekBarVisuals();
            ResetTimelineScrollIfNeeded();
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.MediaPlayer) or nameof(MainWindowViewModel.PlaybackVideoPath))
        {
            BindManagedVideoViews();
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.TimelineZoom)
            or nameof(MainWindowViewModel.TimelineCanvasWidth)
            or nameof(MainWindowViewModel.TimelineCurrentLineLeft))
        {
            UpdateTimelineZoomSliderVisuals();
            EnsureTimelineCurrentFrameVisible(force: e.PropertyName == nameof(MainWindowViewModel.TimelineZoom));
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.Volume) or nameof(MainWindowViewModel.IsMuted))
        {
            UpdateVolumeBarVisuals();
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.VideoZoom)
            or nameof(MainWindowViewModel.VideoZoomCenterX)
            or nameof(MainWindowViewModel.VideoZoomCenterY)
            or nameof(MainWindowViewModel.VideoWidth)
            or nameof(MainWindowViewModel.VideoHeight))
        {
            UpdateVideoZoomLayout();
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsAnalysisPanelVisible)
            or nameof(MainWindowViewModel.IsEventsPanelVisible)
            or nameof(MainWindowViewModel.IsTimelineVisible)
            or nameof(MainWindowViewModel.IsBroadcastPanelVisible))
        {
            UpdatePanelLayout();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsBroadcastModeProject))
        {
            if (_viewModel?.IsBroadcastModeProject != true)
            {
                StopBroadcastLive();
            }

            _isBroadcastManuallyStopped = false;
            ConfigureBroadcastEntityAvailability();
            UpdateWorkspaceEntityVisibility();
            UpdatePanelLayout();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsBroadcastDvrRunning))
        {
            if (_viewModel?.IsBroadcastDvrRunning != true)
            {
                StopBroadcastLive();
            }

            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsNewProjectDialogOpen) or nameof(MainWindowViewModel.IsStartupScreenOpen) or nameof(MainWindowViewModel.IsExportDialogOpen))
        {
            UpdateVideoSurfaceVisibility();
            if (_viewModel?.IsNewProjectDialogOpen == true)
            {
                Dispatcher.UIThread.Post(() => NewProjectCloseButton.Focus());
            }
            else if (_viewModel?.IsStartupScreenVisible == true)
            {
                Dispatcher.UIThread.Post(() => StartupPrimaryButton.Focus());
            }
            else if (_viewModel?.IsExportDialogOpen == true)
            {
                Dispatcher.UIThread.Post(() => ExportDialogCloseButton.Focus());
            }

            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsPresetEditorOpen) && _viewModel?.IsPresetEditorOpen == true)
        {
            Dispatcher.UIThread.Post(() => PresetEditorCloseButton.Focus());
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsTagEventEditorOpen) && _viewModel?.IsTagEventEditorOpen == true)
        {
            Dispatcher.UIThread.Post(() => TagEventEditorCloseButton.Focus());
            return;
        }

    }

    private void ResetTimelineScrollIfNeeded(bool force = false)
    {
        if (_viewModel is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => EnsureTimelineCurrentFrameVisible(force || _viewModel.TimelineCurrentFrame == 0),
            DispatcherPriority.Loaded);
    }

    private void EnsureTimelineCurrentFrameVisible(bool force = false)
    {
        if (_viewModel is null)
        {
            return;
        }

        var viewportWidth = TimelineHorizontalScrollViewer.Viewport.Width;
        var canvasWidth = _viewModel.TimelineCanvasWidth;
        if (viewportWidth <= 0 || canvasWidth <= viewportWidth || _viewModel.TimelineCurrentFrame <= 0)
        {
            if (TimelineHorizontalScrollViewer.Offset.X != 0d)
            {
                TimelineHorizontalScrollViewer.Offset = new Vector(0, TimelineHorizontalScrollViewer.Offset.Y);
            }

            _viewModel.SetTimelineViewport(viewportWidth, 0d);
            return;
        }

        var currentLeft = _viewModel.TimelineCurrentLineLeft;
        var offsetX = TimelineHorizontalScrollViewer.Offset.X;
        var margin = Math.Max(48d, viewportWidth * 0.2d);
        var isCurrentVisible = currentLeft >= offsetX + margin
            && currentLeft <= offsetX + viewportWidth - margin;
        if (!force && isCurrentVisible)
        {
            return;
        }

        var maxOffsetX = Math.Max(0d, canvasWidth - viewportWidth);
        var nextOffsetX = Math.Clamp(currentLeft - (viewportWidth * 0.5d), 0d, maxOffsetX);
        if (Math.Abs(nextOffsetX - offsetX) > 0.5d)
        {
            TimelineHorizontalScrollViewer.Offset = new Vector(nextOffsetX, TimelineHorizontalScrollViewer.Offset.Y);
        }
        else
        {
            _viewModel.SetTimelineViewport(viewportWidth, nextOffsetX);
        }
    }

    private async void OnBrowseNewProjectVideoClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select video file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files")
                {
                    Patterns = ["*.mp4", "*.mov", "*.avi", "*.mkv", "*.m4v"]
                },
                FilePickerFileTypes.All
            ]
        });

        var file = files.FirstOrDefault();
        var localPath = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            _viewModel.NewProjectVideoPath = localPath;
        }
    }

    private void UpdateAnalysisPanelVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        var isVisible = _viewModel.IsAnalysisPanelVisible;
        AnalysisPanel.IsVisible = isVisible;
        AnalysisPanelSplitter.IsVisible = isVisible;

        if (isVisible)
        {
            AnalysisPanelColumn.MinWidth = 280;
            LeftPanelColumn.Width = new GridLength(3.2, GridUnitType.Star);
            EventsPanelColumn.Width = new GridLength(1.05, GridUnitType.Star);
            AnalysisPanelSplitterColumn.Width = new GridLength(6);
            AnalysisPanelColumn.Width = new GridLength(1.45, GridUnitType.Star);
            return;
        }

        _lastVisibleLeftPanelWidth = LeftPanelColumn.ActualWidth > 0
            ? LeftPanelColumn.ActualWidth
            : _lastVisibleLeftPanelWidth;

        AnalysisPanelColumn.MinWidth = 0;
        LeftPanelColumn.Width = _lastVisibleLeftPanelWidth > 0
            ? new GridLength(_lastVisibleLeftPanelWidth, GridUnitType.Pixel)
            : new GridLength(3.2, GridUnitType.Star);
        EventsPanelColumn.Width = new GridLength(1, GridUnitType.Star);
        AnalysisPanelSplitterColumn.Width = new GridLength(0);
        AnalysisPanelColumn.Width = new GridLength(0);
    }


    private void UpdatePanelLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        ResetPanelGridPlacements();
        HideSlotPlaceholders();

        if (_isPlayerFullscreen)
        {
            TimelinePanel.IsVisible = false;
            TimelinePanelSplitter.IsVisible = false;
            EventsPanel.IsVisible = false;
            EventsPanelSplitter.IsVisible = false;
            AnalysisPanel.IsVisible = false;
            AnalysisPanelSplitter.IsVisible = false;
            BroadcastPanel.IsVisible = false;
            StopBroadcastLive();

            ApplyBounds(PlayerPanel, new WorkspaceSlotBounds(0, 0, 3, 5));

            TopContentRow.MinHeight = 0;
            TopContentRow.Height = new GridLength(1, GridUnitType.Star);
            TimelineSplitterRow.Height = new GridLength(0);
            TimelineRow.MinHeight = 0;
            TimelineRow.Height = new GridLength(0);

            SetColumnWidths(
                new GridLength(1, GridUnitType.Star),
                new GridLength(0),
                new GridLength(0),
                new GridLength(0),
                new GridLength(0));
            return;
        }

        if (_maximizedPanel is not null)
        {
            ApplyMaximizedPanelLayout(_maximizedPanel);
            return;
        }

        ConfigureWorkspaceGrid();
        ApplyWorkspaceSplitters();
        UpdateWorkspaceEntityVisibility();

        PlayerPanel.IsVisible = IsEntityVisibleInLayout(WorkspaceEntityKind.Player);
        TimelinePanel.IsVisible = IsEntityVisibleInLayout(WorkspaceEntityKind.Timeline);
        EventsPanel.IsVisible = IsEntityVisibleInLayout(WorkspaceEntityKind.EventsClips);
        AnalysisPanel.IsVisible = IsEntityVisibleInLayout(WorkspaceEntityKind.Statistics);
        BroadcastPanel.IsVisible = IsEntityVisibleInLayout(WorkspaceEntityKind.Broadcast);

        ApplyEntityBounds(WorkspaceEntityKind.Player, PlayerPanel);
        ApplyEntityBounds(WorkspaceEntityKind.Timeline, TimelinePanel);
        ApplyEntityBounds(WorkspaceEntityKind.EventsClips, EventsPanel);
        ApplyEntityBounds(WorkspaceEntityKind.Statistics, AnalysisPanel);
        ApplyEntityBounds(WorkspaceEntityKind.Broadcast, BroadcastPanel);
        ApplyEmptySlotPlaceholders();
        if (BroadcastPanel.IsVisible)
        {
            QueueBroadcastRendererInitialization(waitForRender: true);
            _ = EnsureBroadcastDisplayAsync();
        }
        else
        {
            StopBroadcastLive();
        }
    }

    private void ResetPanelGridPlacements()
    {
        foreach (var placeholder in GetSlotPlaceholders())
        {
            ApplyBounds(placeholder, new WorkspaceSlotBounds(0, 0, 1, 1));
        }

        ApplyBounds(PlayerPanel, new WorkspaceSlotBounds(0, 0, 1, 1));
        ApplyBounds(EventsPanel, new WorkspaceSlotBounds(0, 2, 3, 1));
        ApplyBounds(AnalysisPanel, new WorkspaceSlotBounds(0, 4, 3, 1));
        ApplyBounds(TimelinePanel, new WorkspaceSlotBounds(2, 0, 1, 1));
        ApplyBounds(BroadcastPanel, new WorkspaceSlotBounds(2, 2, 1, 1));
    }

    private void ConfigureWorkspaceGrid()
    {
        LeftPanelColumn.MinWidth = 0;
        EventsPanelColumn.MinWidth = 0;
        AnalysisPanelColumn.MinWidth = 0;
        TopContentRow.MinHeight = 0;
        TimelineRow.MinHeight = 0;

        switch (_workspaceLayoutKind)
        {
            case WorkspaceLayoutKind.Single:
                SetColumnWidths(new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0), new GridLength(0), new GridLength(0));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0));
                break;
            case WorkspaceLayoutKind.SplitRows:
                SetColumnWidths(new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0), new GridLength(0), new GridLength(0));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star));
                break;
            case WorkspaceLayoutKind.TopTwoBottom:
            case WorkspaceLayoutKind.BottomTwo:
                SetColumnWidths(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(260, GridUnitType.Pixel));
                break;
            case WorkspaceLayoutKind.FourGrid:
                SetColumnWidths(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star));
                break;
            case WorkspaceLayoutKind.FiveWindows:
                SetColumnWidths(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star));
                break;
            case WorkspaceLayoutKind.SplitColumns:
                SetColumnWidths(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0));
                break;
            default:
                LeftPanelColumn.MinWidth = 420;
                EventsPanelColumn.MinWidth = 250;
                AnalysisPanelColumn.MinWidth = 280;
                TopContentRow.MinHeight = 260;
                TimelineRow.MinHeight = 170;
                SetColumnWidths(
                    new GridLength(3.2, GridUnitType.Star),
                    new GridLength(6),
                    new GridLength(1.05, GridUnitType.Star),
                    new GridLength(6),
                    new GridLength(1.45, GridUnitType.Star));
                SetRowHeights(new GridLength(1, GridUnitType.Star), new GridLength(6), new GridLength(240, GridUnitType.Pixel));
                break;
        }
    }

    private void ApplyWorkspaceSplitters()
    {
        EventsPanelSplitter.IsVisible = false;
        AnalysisPanelSplitter.IsVisible = false;
        TimelinePanelSplitter.IsVisible = false;
        HideWorkspaceAddButtons();

        switch (_workspaceLayoutKind)
        {
            case WorkspaceLayoutKind.Single:
                break;
            case WorkspaceLayoutKind.Reference:
                PlaceVerticalSplitter(EventsPanelSplitter, 1, 0, 3);
                PlaceVerticalSplitter(AnalysisPanelSplitter, 3, 0, 3);
                PlaceHorizontalSplitter(TimelinePanelSplitter, 1, 0, 1);
                PlaceWorkspaceAddButton(EventsSplitterAddWindowButton, 1, 0, 3, 1);
                PlaceWorkspaceAddButton(AnalysisSplitterAddWindowButton, 3, 0, 3, 1);
                PlaceWorkspaceAddButton(TimelineSplitterAddWindowButton, 0, 1, 1, 1);
                break;
            case WorkspaceLayoutKind.SplitRows:
                PlaceHorizontalSplitter(TimelinePanelSplitter, 1, 0, 5);
                PlaceWorkspaceAddButton(TimelineSplitterAddWindowButton, 0, 1, 1, 5);
                break;
            case WorkspaceLayoutKind.TopTwoBottom:
                PlaceVerticalSplitter(EventsPanelSplitter, 1, 0, 1);
                PlaceHorizontalSplitter(TimelinePanelSplitter, 1, 0, 3);
                PlaceWorkspaceAddButton(EventsSplitterAddWindowButton, 1, 0, 1, 1);
                PlaceWorkspaceAddButton(TimelineSplitterAddWindowButton, 0, 1, 1, 3);
                break;
            case WorkspaceLayoutKind.FourGrid:
                PlaceVerticalSplitter(EventsPanelSplitter, 1, 0, 3);
                PlaceHorizontalSplitter(TimelinePanelSplitter, 1, 0, 3);
                PlaceWorkspaceAddButton(EventsSplitterAddWindowButton, 1, 0, 3, 1);
                PlaceWorkspaceAddButton(TimelineSplitterAddWindowButton, 0, 1, 1, 3);
                break;
            case WorkspaceLayoutKind.FiveWindows:
                PlaceVerticalSplitter(EventsPanelSplitter, 1, 0, 1);
                PlaceVerticalSplitter(AnalysisPanelSplitter, 3, 0, 3);
                PlaceHorizontalSplitter(TimelinePanelSplitter, 1, 0, 5);
                PlaceWorkspaceAddButton(EventsSplitterAddWindowButton, 1, 0, 1, 1);
                PlaceWorkspaceAddButton(AnalysisSplitterAddWindowButton, 3, 0, 3, 1);
                PlaceWorkspaceAddButton(TimelineSplitterAddWindowButton, 0, 1, 1, 5);
                break;
            case WorkspaceLayoutKind.SplitColumns:
                PlaceVerticalSplitter(EventsPanelSplitter, 1, 0, 3);
                PlaceWorkspaceAddButton(EventsSplitterAddWindowButton, 1, 0, 3, 1);
                break;
            case WorkspaceLayoutKind.BottomTwo:
                PlaceHorizontalSplitter(TimelinePanelSplitter, 1, 0, 3);
                PlaceVerticalSplitter(EventsPanelSplitter, 1, 2, 1);
                PlaceWorkspaceAddButton(TimelineSplitterAddWindowButton, 0, 1, 1, 3);
                PlaceWorkspaceAddButton(EventsSplitterAddWindowButton, 1, 2, 1, 1);
                break;
        }
    }

    private void ApplyEntityBounds(WorkspaceEntityKind entity, Control panel)
    {
        if (!_entitySlots.TryGetValue(entity, out var slotIndex))
        {
            return;
        }

        var bounds = GetActiveSlotBounds();
        if (slotIndex < 0 || slotIndex >= bounds.Length)
        {
            return;
        }

        ApplyBounds(panel, bounds[slotIndex]);
    }

    private void ApplyEmptySlotPlaceholders()
    {
        var bounds = GetActiveSlotBounds();
        var placeholders = GetSlotPlaceholders();

        for (var index = 0; index < placeholders.Length; index++)
        {
            var placeholder = placeholders[index];
            var isActive = index < bounds.Length;
            var isEmpty = !_entitySlots.Any(pair => pair.Value == index);
            placeholder.IsVisible = isActive && isEmpty;

            if (placeholder.IsVisible)
            {
                ApplyBounds(placeholder, bounds[index]);
            }
        }
    }

    private void NormalizeEntitySlotsForLayout()
    {
        var activeSlotCount = GetActiveSlotBounds().Length;
        var entities = _entitySlots
            .OrderBy(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        _entitySlots.Clear();

        for (var index = 0; index < Math.Min(activeSlotCount, entities.Count); index++)
        {
            _entitySlots[entities[index]] = index;
        }
    }

    private void RemoveEmptyWorkspaceSlot(int slotIndex)
    {
        var activeSlotCount = GetActiveSlotBounds().Length;
        if (slotIndex < 0
            || slotIndex >= activeSlotCount
            || activeSlotCount <= 1
            || _entitySlots.Any(pair => pair.Value == slotIndex))
        {
            return;
        }

        var existingEntities = _entitySlots.Keys.ToArray();
        var beforeRects = CaptureWorkspacePanelRects(existingEntities);
        var nextSlotCount = activeSlotCount - 1;
        var nextSlots = _entitySlots
            .Select(pair => new
            {
                pair.Key,
                Slot = pair.Value > slotIndex ? pair.Value - 1 : pair.Value
            })
            .ToList();

        _workspaceLayoutKind = GetLayoutKindForSlotCount(nextSlotCount);
        _entitySlots.Clear();

        foreach (var item in nextSlots)
        {
            if (item.Slot >= 0 && item.Slot < nextSlotCount)
            {
                _entitySlots[item.Key] = item.Slot;
            }
        }

        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
        AnimateWorkspacePanelPlacement(beforeRects);

        if (_viewModel is not null)
        {
            _viewModel.StatusMessage = "Пустое окно убрано.";
        }
    }

    private static WorkspaceLayoutKind GetLayoutKindForSlotCount(int slotCount)
    {
        return slotCount switch
        {
            <= 1 => WorkspaceLayoutKind.Single,
            2 => WorkspaceLayoutKind.SplitColumns,
            3 => WorkspaceLayoutKind.TopTwoBottom,
            4 => WorkspaceLayoutKind.FourGrid,
            _ => WorkspaceLayoutKind.FiveWindows
        };
    }

    private void ConfigureBroadcastEntityAvailability()
    {
        if (_viewModel?.IsBroadcastModeProject == true)
        {
            if (!_entitySlots.ContainsKey(WorkspaceEntityKind.Broadcast))
            {
                AssignEntityToSlot(WorkspaceEntityKind.Broadcast, FindFirstAvailableActiveSlot());
            }

            return;
        }

        if (_entitySlots.Remove(WorkspaceEntityKind.Broadcast))
        {
            StopBroadcastLive();
        }

        if (!_entitySlots.ContainsKey(WorkspaceEntityKind.Statistics))
        {
            AssignEntityToSlot(WorkspaceEntityKind.Statistics, FindFirstAvailableActiveSlot());
        }
    }

    private void AssignEntityToSlot(WorkspaceEntityKind entity, int slotIndex)
    {
        var activeSlotCount = GetActiveSlotBounds().Length;
        if (activeSlotCount == 0)
        {
            return;
        }

        slotIndex = Math.Clamp(slotIndex, 0, activeSlotCount - 1);

        foreach (var existingEntity in _entitySlots.Where(pair => pair.Value == slotIndex).Select(pair => pair.Key).ToList())
        {
            _entitySlots.Remove(existingEntity);
        }

        _entitySlots[entity] = slotIndex;
    }

    private void MoveWorkspaceEntityToSlot(WorkspaceEntityKind entity, int targetSlotIndex)
    {
        var activeSlotCount = GetActiveSlotBounds().Length;
        if (!_entitySlots.TryGetValue(entity, out var sourceSlotIndex)
            || targetSlotIndex < 0
            || targetSlotIndex >= activeSlotCount
            || targetSlotIndex == sourceSlotIndex)
        {
            return;
        }

        WorkspaceEntityKind? targetEntity = null;
        foreach (var pair in _entitySlots)
        {
            if (pair.Value == targetSlotIndex && pair.Key != entity)
            {
                targetEntity = pair.Key;
                break;
            }
        }

        var animatedEntities = targetEntity is { } swapTarget
            ? new[] { entity, swapTarget }
            : new[] { entity };
        var beforeRects = CaptureWorkspacePanelRects(animatedEntities);

        _entitySlots[entity] = targetSlotIndex;
        if (targetEntity is { } swapEntity)
        {
            _entitySlots[swapEntity] = sourceSlotIndex;
        }

        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
        AnimateWorkspacePanelPlacement(beforeRects);

        if (_viewModel is not null)
        {
            _viewModel.StatusMessage = targetEntity is null
                ? "Окно перемещено."
                : "Окна поменяны местами.";
        }
    }

    private Dictionary<WorkspaceEntityKind, Rect> CaptureWorkspacePanelRects(IEnumerable<WorkspaceEntityKind> entities)
    {
        var rects = new Dictionary<WorkspaceEntityKind, Rect>();
        foreach (var entity in entities.Distinct())
        {
            var panel = GetWorkspaceEntityPanel(entity);
            if (panel.IsVisible && TryGetControlRect(panel, out var rect))
            {
                rects[entity] = rect;
            }
        }

        return rects;
    }

    private async void AnimateWorkspacePanelPlacement(IReadOnlyDictionary<WorkspaceEntityKind, Rect> beforeRects)
    {
        if (beforeRects.Count == 0)
        {
            return;
        }

        var animationId = ++_workspacePanelAnimationVersion;
        var animatedPanels = new List<(Control Panel, double DeltaX, double DeltaY)>();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (animationId != _workspacePanelAnimationVersion)
            {
                return;
            }

            foreach (var (entity, beforeRect) in beforeRects)
            {
                var panel = GetWorkspaceEntityPanel(entity);
                if (!panel.IsVisible || !TryGetControlRect(panel, out var afterRect))
                {
                    continue;
                }

                var deltaX = beforeRect.X - afterRect.X;
                var deltaY = beforeRect.Y - afterRect.Y;
                if ((Math.Abs(deltaX) + Math.Abs(deltaY)) < 1d)
                {
                    continue;
                }

                panel.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                panel.RenderTransform = new TranslateTransform(deltaX, deltaY);
                panel.Opacity = 0.92;
                animatedPanels.Add((panel, deltaX, deltaY));
            }
        }, DispatcherPriority.Render);

        if (animatedPanels.Count == 0)
        {
            return;
        }

        const int frames = 10;
        const int frameDelayMs = 14;

        for (var frame = 1; frame <= frames; frame++)
        {
            await Task.Delay(frameDelayMs);
            var progress = frame / (double)frames;
            var eased = 1d - Math.Pow(1d - progress, 3d);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (animationId != _workspacePanelAnimationVersion)
                {
                    return;
                }

                foreach (var (panel, deltaX, deltaY) in animatedPanels)
                {
                    var remaining = 1d - eased;
                    panel.RenderTransform = new TranslateTransform(deltaX * remaining, deltaY * remaining);
                    panel.Opacity = 0.92 + (0.08 * eased);
                }
            }, DispatcherPriority.Render);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (animationId != _workspacePanelAnimationVersion)
            {
                return;
            }

            foreach (var (panel, _, _) in animatedPanels)
            {
                panel.RenderTransform = null;
                panel.Opacity = 1d;
            }
        }, DispatcherPriority.Render);
    }

    private bool TryGetControlRect(Control control, out Rect rect)
    {
        var origin = control.TranslatePoint(new Point(0, 0), MainLayoutGrid);
        if (origin is not { } topLeft)
        {
            rect = default;
            return false;
        }

        rect = new Rect(topLeft, control.Bounds.Size);
        return true;
    }

    private void ToggleEntityVisibility(WorkspaceEntityKind entity)
    {
        if (entity == WorkspaceEntityKind.Broadcast && _viewModel?.IsBroadcastModeProject != true)
        {
            return;
        }

        if (IsEntityVisibleInLayout(entity))
        {
            _entitySlots.Remove(entity);
        }
        else
        {
            AssignEntityToSlot(entity, FindFirstAvailableActiveSlot());
        }

        UpdateWorkspaceEntityVisibility();
        UpdateVideoSurfaceVisibility();
        UpdatePanelLayout();
    }

    private int FindFirstAvailableActiveSlot()
    {
        var activeSlotCount = GetActiveSlotBounds().Length;
        for (var index = 0; index < activeSlotCount; index++)
        {
            if (!_entitySlots.Any(pair => pair.Value == index))
            {
                return index;
            }
        }

        return Math.Max(0, activeSlotCount - 1);
    }

    private WorkspaceEntityKind? FindFirstMissingAvailableEntity()
    {
        foreach (var entity in WorkspaceEntityOrder)
        {
            if (entity == WorkspaceEntityKind.Broadcast && _viewModel?.IsBroadcastModeProject != true)
            {
                continue;
            }

            if (!_entitySlots.ContainsKey(entity))
            {
                return entity;
            }
        }

        return null;
    }

    private bool TryGetWorkspaceSlotAtPoint(Point point, out int slotIndex)
    {
        var bounds = GetActiveSlotBounds();
        for (var index = 0; index < bounds.Length; index++)
        {
            var slotControl = GetWorkspaceSlotControl(index);
            if (slotControl is null
                || !slotControl.IsVisible
                || slotControl.Bounds.Width <= 0
                || slotControl.Bounds.Height <= 0)
            {
                continue;
            }

            var topLeft = slotControl.TranslatePoint(new Point(0, 0), MainLayoutGrid);
            if (topLeft is not { } origin)
            {
                continue;
            }

            var slotRect = new Rect(origin, slotControl.Bounds.Size);
            if (slotRect.Contains(point))
            {
                slotIndex = index;
                return true;
            }
        }

        slotIndex = -1;
        return false;
    }

    private Control? GetWorkspaceSlotControl(int slotIndex)
    {
        if (TryGetEntityAtSlot(slotIndex, out var entity))
        {
            return GetWorkspaceEntityPanel(entity);
        }

        var placeholders = GetSlotPlaceholders();
        return slotIndex >= 0 && slotIndex < placeholders.Length
            ? placeholders[slotIndex]
            : null;
    }

    private bool TryGetEntityAtSlot(int slotIndex, out WorkspaceEntityKind entity)
    {
        foreach (var pair in _entitySlots)
        {
            if (pair.Value == slotIndex && IsEntityVisibleInLayout(pair.Key))
            {
                entity = pair.Key;
                return true;
            }
        }

        entity = default;
        return false;
    }

    private Control GetWorkspaceEntityPanel(WorkspaceEntityKind entity)
    {
        return entity switch
        {
            WorkspaceEntityKind.Player => PlayerPanel,
            WorkspaceEntityKind.Timeline => TimelinePanel,
            WorkspaceEntityKind.EventsClips => EventsPanel,
            WorkspaceEntityKind.Statistics => AnalysisPanel,
            WorkspaceEntityKind.Broadcast => BroadcastPanel,
            _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null)
        };
    }

    private bool IsEntityVisibleInLayout(WorkspaceEntityKind entity)
    {
        return _entitySlots.TryGetValue(entity, out var slotIndex)
            && slotIndex >= 0
            && slotIndex < GetActiveSlotBounds().Length;
    }

    private void UpdateWorkspaceEntityVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        _isPlayerPanelHidden = !IsEntityVisibleInLayout(WorkspaceEntityKind.Player);
        _viewModel.IsPlayerPanelHidden = _isPlayerPanelHidden;
        _viewModel.IsTimelineHidden = !IsEntityVisibleInLayout(WorkspaceEntityKind.Timeline);
        _viewModel.IsEventsPanelHidden = !IsEntityVisibleInLayout(WorkspaceEntityKind.EventsClips);
        _viewModel.IsAnalysisPanelHidden = !IsEntityVisibleInLayout(WorkspaceEntityKind.Statistics);
        _viewModel.IsBroadcastPanelHidden = !IsEntityVisibleInLayout(WorkspaceEntityKind.Broadcast);
    }

    private WorkspaceSlotBounds[] GetActiveSlotBounds()
    {
        return _workspaceLayoutKind switch
        {
            WorkspaceLayoutKind.Single =>
            [
                new WorkspaceSlotBounds(0, 0, 3, 5)
            ],
            WorkspaceLayoutKind.SplitRows =>
            [
                new WorkspaceSlotBounds(0, 0, 1, 5),
                new WorkspaceSlotBounds(2, 0, 1, 5)
            ],
            WorkspaceLayoutKind.TopTwoBottom =>
            [
                new WorkspaceSlotBounds(0, 0, 1, 1),
                new WorkspaceSlotBounds(0, 2, 1, 1),
                new WorkspaceSlotBounds(2, 0, 1, 3)
            ],
            WorkspaceLayoutKind.FourGrid =>
            [
                new WorkspaceSlotBounds(0, 0, 1, 1),
                new WorkspaceSlotBounds(0, 2, 1, 1),
                new WorkspaceSlotBounds(2, 0, 1, 1),
                new WorkspaceSlotBounds(2, 2, 1, 1)
            ],
            WorkspaceLayoutKind.FiveWindows =>
            [
                new WorkspaceSlotBounds(0, 0, 1, 1),
                new WorkspaceSlotBounds(0, 2, 1, 1),
                new WorkspaceSlotBounds(0, 4, 1, 1),
                new WorkspaceSlotBounds(2, 0, 1, 3),
                new WorkspaceSlotBounds(2, 4, 1, 1)
            ],
            WorkspaceLayoutKind.SplitColumns =>
            [
                new WorkspaceSlotBounds(0, 0, 3, 1),
                new WorkspaceSlotBounds(0, 2, 3, 1)
            ],
            WorkspaceLayoutKind.BottomTwo =>
            [
                new WorkspaceSlotBounds(0, 0, 1, 3),
                new WorkspaceSlotBounds(2, 0, 1, 1),
                new WorkspaceSlotBounds(2, 2, 1, 1)
            ],
            _ =>
            [
                new WorkspaceSlotBounds(0, 0, 1, 1),
                new WorkspaceSlotBounds(2, 0, 1, 1),
                new WorkspaceSlotBounds(0, 2, 3, 1),
                new WorkspaceSlotBounds(0, 4, 3, 1)
            ]
        };
    }

    private Border[] GetSlotPlaceholders()
    {
        return [SlotPlaceholder0, SlotPlaceholder1, SlotPlaceholder2, SlotPlaceholder3, SlotPlaceholder4];
    }

    private void HideSlotPlaceholders()
    {
        foreach (var placeholder in GetSlotPlaceholders())
        {
            placeholder.IsVisible = false;
        }
    }

    private static void ApplyBounds(Control control, WorkspaceSlotBounds bounds)
    {
        Grid.SetRow(control, bounds.Row);
        Grid.SetColumn(control, bounds.Column);
        Grid.SetRowSpan(control, bounds.RowSpan);
        Grid.SetColumnSpan(control, bounds.ColumnSpan);
    }

    private void SetColumnWidths(GridLength first, GridLength firstSplitter, GridLength second, GridLength secondSplitter, GridLength third)
    {
        LeftPanelColumn.Width = first;
        EventsPanelSplitterColumn.Width = firstSplitter;
        EventsPanelColumn.Width = second;
        AnalysisPanelSplitterColumn.Width = secondSplitter;
        AnalysisPanelColumn.Width = third;
    }

    private void SetRowHeights(GridLength top, GridLength splitter, GridLength bottom)
    {
        TopContentRow.Height = top;
        TimelineSplitterRow.Height = splitter;
        TimelineRow.Height = bottom;
    }

    private static void PlaceVerticalSplitter(GridSplitter splitter, int column, int row, int rowSpan)
    {
        splitter.IsVisible = true;
        Grid.SetColumn(splitter, column);
        Grid.SetColumnSpan(splitter, 1);
        Grid.SetRow(splitter, row);
        Grid.SetRowSpan(splitter, rowSpan);
    }

    private static void PlaceHorizontalSplitter(GridSplitter splitter, int row, int column, int columnSpan)
    {
        splitter.IsVisible = true;
        Grid.SetRow(splitter, row);
        Grid.SetRowSpan(splitter, 1);
        Grid.SetColumn(splitter, column);
        Grid.SetColumnSpan(splitter, columnSpan);
    }

    private static void PlaceWorkspaceAddButton(Button button, int column, int row, int rowSpan, int columnSpan)
    {
        Grid.SetColumn(button, column);
        Grid.SetColumnSpan(button, columnSpan);
        Grid.SetRow(button, row);
        Grid.SetRowSpan(button, rowSpan);
        button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        button.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    private static bool TryGetPanelEntity(string panel, out WorkspaceEntityKind entity)
    {
        entity = panel switch
        {
            PanelPlayer => WorkspaceEntityKind.Player,
            PanelTimeline => WorkspaceEntityKind.Timeline,
            PanelEvents => WorkspaceEntityKind.EventsClips,
            PanelAnalysis => WorkspaceEntityKind.Statistics,
            PanelBroadcast => WorkspaceEntityKind.Broadcast,
            _ => default
        };

        return string.Equals(panel, PanelPlayer, StringComparison.Ordinal)
            || string.Equals(panel, PanelTimeline, StringComparison.Ordinal)
            || string.Equals(panel, PanelEvents, StringComparison.Ordinal)
            || string.Equals(panel, PanelAnalysis, StringComparison.Ordinal)
            || string.Equals(panel, PanelBroadcast, StringComparison.Ordinal);
    }

    private void ApplyMaximizedPanelLayout(string panel)
    {
        PlayerPanel.IsVisible = string.Equals(panel, PanelPlayer, StringComparison.Ordinal);
        TimelinePanel.IsVisible = string.Equals(panel, PanelTimeline, StringComparison.Ordinal);
        EventsPanel.IsVisible = string.Equals(panel, PanelEvents, StringComparison.Ordinal);
        AnalysisPanel.IsVisible = string.Equals(panel, PanelAnalysis, StringComparison.Ordinal);
        BroadcastPanel.IsVisible = string.Equals(panel, PanelBroadcast, StringComparison.Ordinal);

        TimelinePanelSplitter.IsVisible = false;
        EventsPanelSplitter.IsVisible = false;
        AnalysisPanelSplitter.IsVisible = false;

        var target = panel switch
        {
            PanelPlayer => PlayerPanel,
            PanelTimeline => TimelinePanel,
            PanelEvents => EventsPanel,
            PanelAnalysis => AnalysisPanel,
            PanelBroadcast => BroadcastPanel,
            _ => null
        };

        if (target is null)
        {
            _maximizedPanel = null;
            UpdatePanelLayout();
            return;
        }

        Grid.SetColumn(target, 0);
        Grid.SetColumnSpan(target, 5);
        Grid.SetRow(target, 0);
        Grid.SetRowSpan(target, 3);

        TopContentRow.MinHeight = 0;
        TopContentRow.Height = new GridLength(1, GridUnitType.Star);
        TimelineSplitterRow.Height = new GridLength(0);
        TimelineRow.MinHeight = 0;
        TimelineRow.Height = new GridLength(0);

        LeftPanelColumn.MinWidth = 0;
        LeftPanelColumn.Width = new GridLength(1, GridUnitType.Star);
        EventsPanelSplitterColumn.Width = new GridLength(0);
        EventsPanelColumn.MinWidth = 0;
        EventsPanelColumn.Width = new GridLength(0);
        AnalysisPanelSplitterColumn.Width = new GridLength(0);
        AnalysisPanelColumn.MinWidth = 0;
        AnalysisPanelColumn.Width = new GridLength(0);

        if (string.Equals(panel, PanelBroadcast, StringComparison.Ordinal))
        {
            QueueBroadcastRendererInitialization(waitForRender: true);
            _ = EnsureBroadcastDisplayAsync();
        }
    }

    private void ApplyPlayerFullscreenState()
    {
        if (_isPlayerFullscreen)
        {
            _windowStateBeforePlayerFullscreen = WindowState;
            _mainLayoutMarginBeforePlayerFullscreen = MainLayoutGrid.Margin;
            TopMenuBar.IsVisible = false;
            MainLayoutGrid.Margin = new Thickness(0);
            WindowState = WindowState.FullScreen;
            return;
        }

        TopMenuBar.IsVisible = true;
        MainLayoutGrid.Margin = _mainLayoutMarginBeforePlayerFullscreen;
        WindowState = _windowStateBeforePlayerFullscreen == WindowState.FullScreen
            ? WindowState.Maximized
            : _windowStateBeforePlayerFullscreen;
    }

    private void UpdateSeekBarVisuals()
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = SeekBarRoot.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var duration = Math.Max(1, _viewModel.DurationFrames);
        var ratio = Math.Clamp(_viewModel.CurrentFrame / (double)duration, 0d, 1d);
        var progressWidth = width * ratio;
        SeekBarProgress.Width = progressWidth;

        var thumbWidth = SeekBarThumb.Bounds.Width > 0 ? SeekBarThumb.Bounds.Width : SeekBarThumb.Width;
        var thumbX = Math.Clamp(progressWidth - (thumbWidth / 2d), 0d, Math.Max(0d, width - thumbWidth));
        SeekBarThumb.RenderTransform = new TranslateTransform(thumbX, 0);
    }

    private void UpdateVolumeBarVisuals()
    {
        if (_viewModel is null)
        {
            return;
        }

        var height = VolumeBarRoot.Bounds.Height;
        if (height <= 0)
        {
            return;
        }

        var thumbHeight = VolumeBarThumb.Bounds.Height > 0 ? VolumeBarThumb.Bounds.Height : VolumeBarThumb.Height;
        var usableHeight = Math.Max(0d, height - thumbHeight);
        var ratio = Math.Clamp(_viewModel.Volume / 100d, 0d, 1d);
        var thumbY = (1d - ratio) * usableHeight;
        var progressHeight = ratio * usableHeight + (thumbHeight / 2d);

        VolumeBarProgress.Height = Math.Clamp(progressHeight, 0d, height);
        VolumeBarThumb.RenderTransform = new TranslateTransform(0, thumbY);
    }

    private void UpdateTimelineZoomSliderVisuals()
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = TimelineZoomSliderRoot.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var trackWidth = Math.Max(1d, width - 10d);
        var minZoom = _viewModel.TimelineZoomMinimumValue;
        var maxZoom = Math.Max(minZoom + 0.001d, _viewModel.TimelineZoomMaximumValue);
        var ratio = Math.Clamp((_viewModel.TimelineZoom - minZoom) / (maxZoom - minZoom), 0d, 1d);
        var progressWidth = trackWidth * ratio;

        TimelineZoomSliderProgress.Width = progressWidth;
        var thumbWidth = TimelineZoomSliderThumb.Bounds.Width > 0 ? TimelineZoomSliderThumb.Bounds.Width : TimelineZoomSliderThumb.Width;
        var thumbX = Math.Clamp(progressWidth - (thumbWidth / 2d), 0d, Math.Max(0d, trackWidth - thumbWidth));
        TimelineZoomSliderThumb.RenderTransform = new TranslateTransform(thumbX, 0);
    }

    private void SetVolumeFromPointer(PointerEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var height = VolumeBarRoot.Bounds.Height;
        if (height <= 0)
        {
            return;
        }

        var thumbHeight = VolumeBarThumb.Bounds.Height > 0 ? VolumeBarThumb.Bounds.Height : VolumeBarThumb.Height;
        var usableHeight = Math.Max(1d, height - thumbHeight);
        var point = e.GetPosition(VolumeBarRoot);
        var ratio = 1d - Math.Clamp((point.Y - (thumbHeight / 2d)) / usableHeight, 0d, 1d);
        _viewModel.Volume = (int)Math.Round(ratio * 100d);
        UpdateVolumeBarVisuals();
    }

    private void SetTimelineZoomFromPointer(PointerEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = TimelineZoomSliderRoot.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var trackWidth = Math.Max(1d, width - 10d);
        var point = e.GetPosition(TimelineZoomSliderRoot);
        var ratio = Math.Clamp((point.X - 5d) / trackWidth, 0d, 1d);
        _viewModel.TimelineZoom = _viewModel.TimelineZoomMinimumValue
            + ratio * (_viewModel.TimelineZoomMaximumValue - _viewModel.TimelineZoomMinimumValue);
        UpdateTimelineZoomSliderVisuals();
        EnsureTimelineCurrentFrameVisible(force: true);
    }

    private void CloseOtherMenus(ToggleButton activeButton)
    {
        if (_isSynchronizingMenus)
        {
            return;
        }

        _isSynchronizingMenus = true;
        try
        {
            if (!ReferenceEquals(activeButton, FileMenuButton))
            {
                FileMenuButton.IsChecked = false;
            }

            if (!ReferenceEquals(activeButton, MarkupMenuButton))
            {
                MarkupMenuButton.IsChecked = false;
            }

            if (!ReferenceEquals(activeButton, ViewMenuButton))
            {
                ViewMenuButton.IsChecked = false;
            }

            if (!ReferenceEquals(activeButton, SettingsMenuButton))
            {
                SettingsMenuButton.IsChecked = false;
            }

            if (!ReferenceEquals(activeButton, HelpMenuButton))
            {
                HelpMenuButton.IsChecked = false;
            }
        }
        finally
        {
            _isSynchronizingMenus = false;
        }
    }

    private static bool ShouldIgnoreHotkeys(object? source)
    {
        return source is TextBox;
    }

    private static string? TryMapHotkey(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return null;
        }

        var keyText = e.Key.ToString();
        if (string.IsNullOrWhiteSpace(keyText))
        {
            return null;
        }

        if (keyText.Length == 1 && char.IsLetterOrDigit(keyText[0]))
        {
            return keyText.ToUpperInvariant();
        }

        if (keyText.Length == 2 && keyText[0] == 'D' && char.IsDigit(keyText[1]))
        {
            return keyText[1].ToString();
        }

        if (keyText.StartsWith("NumPad", StringComparison.Ordinal) && keyText.Length == "NumPad0".Length)
        {
            var lastChar = keyText[^1];
            if (char.IsDigit(lastChar))
            {
                return lastChar.ToString();
            }
        }

        return null;
    }

    private static string? TryExtractSingleEnglishLetter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        for (var index = text.Length - 1; index >= 0; index--)
        {
            var character = text[index];
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

    private static bool HasVisualAncestor<T>(object? source) where T : class
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().OfType<T>().Any();
    }

    private static bool HasVisualAncestor(Visual target, object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Contains(target);
    }
}








