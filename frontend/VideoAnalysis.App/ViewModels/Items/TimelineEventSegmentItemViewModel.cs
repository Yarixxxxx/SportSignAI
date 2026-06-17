namespace VideoAnalysis.App.ViewModels.Items;

public sealed class TimelineEventSegmentItemViewModel
{
    public required long StartFrame { get; init; }
    public required double Left { get; init; }
    public required double Width { get; init; }
    public required string ColorHex { get; init; }
    public bool IsInstant => Width <= 6;
}
