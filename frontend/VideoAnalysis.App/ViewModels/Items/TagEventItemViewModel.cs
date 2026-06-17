using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoAnalysis.App.ViewModels.Items;

public sealed partial class TagEventItemViewModel : ObservableObject
{
    public required Guid Id { get; init; }
    public required Guid TagPresetId { get; init; }
    public required string PresetName { get; init; }
    public required string TeamSide { get; init; }
    public required long StartFrame { get; init; }
    public required long EndFrame { get; init; }
    public required string StartTimeText { get; init; }
    public required string EndTimeText { get; init; }
    public required string Player { get; init; }
    public required string Period { get; init; }
    public required string Notes { get; init; }

    [ObservableProperty]
    private bool _isSelectedForPlaylist;

    public string PlaylistToggleGlyph => IsSelectedForPlaylist ? "✓" : "+";

    partial void OnIsSelectedForPlaylistChanged(bool value)
    {
        OnPropertyChanged(nameof(PlaylistToggleGlyph));
    }
}
