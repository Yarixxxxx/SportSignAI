using System.Globalization;
using System.Text;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Dtos;
using VideoAnalysis.Core.Enums;

namespace VideoAnalysis.Infrastructure.Services;

public sealed class AnnotationRenderService : IAnnotationRenderService
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public async Task<string?> BuildOverlayFilterScriptAsync(
        IReadOnlyList<AnnotationDto> annotations,
        IReadOnlyList<ClipSegmentDto> segments,
        double framesPerSecond,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (annotations.Count == 0 && segments.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(workingDirectory);
        var scriptPath = Path.Combine(workingDirectory, "overlay_filters.txt");
        var filters = new List<string>(annotations.Count + (segments.Count * 4));

        foreach (var annotation in annotations)
        {
            var startSeconds = annotation.StartFrame / framesPerSecond;
            var endSeconds = annotation.EndFrame / framesPerSecond;
            var enable = $"between(t,{ToInvariant(startSeconds)},{ToInvariant(endSeconds)})";
            var color = NormalizeColor(annotation.ColorHex);

            switch (annotation.ShapeType)
            {
                case AnnotationShapeType.Arrow:
                    filters.Add($"drawtext=text='->':x={ToInvariant(annotation.X1)}:y={ToInvariant(annotation.Y1)}:fontsize=42:fontcolor={color}:enable='{enable}'");
                    break;

                case AnnotationShapeType.Circle:
                    var left = Math.Min(annotation.X1, annotation.X2);
                    var top = Math.Min(annotation.Y1, annotation.Y2);
                    var width = Math.Abs(annotation.X2 - annotation.X1);
                    var height = Math.Abs(annotation.Y2 - annotation.Y1);
                    filters.Add($"drawbox=x={ToInvariant(left)}:y={ToInvariant(top)}:w={ToInvariant(width)}:h={ToInvariant(height)}:color={color}:t={ToInvariant(annotation.StrokeWidth)}:enable='{enable}'");
                    break;

                case AnnotationShapeType.Text:
                    var text = EscapeText(string.IsNullOrWhiteSpace(annotation.Text) ? "NOTE" : annotation.Text!);
                    filters.Add($"drawtext=text='{text}':x={ToInvariant(annotation.X1)}:y={ToInvariant(annotation.Y1)}:fontsize=30:fontcolor={color}:enable='{enable}'");
                    break;
            }
        }

        AppendSegmentInfoFilters(filters, segments, framesPerSecond);

        if (filters.Count == 0)
        {
            return null;
        }

        filters.Insert(0, "setpts=PTS-STARTPTS");
        await File.WriteAllTextAsync(scriptPath, string.Join(',', filters), Utf8WithoutBom, cancellationToken);
        return scriptPath;
    }

    private static string NormalizeColor(string input)
    {
        return string.IsNullOrWhiteSpace(input) ? "white" : input.Trim().TrimStart('#');
    }

    private static void AppendSegmentInfoFilters(List<string> filters, IReadOnlyList<ClipSegmentDto> segments, double framesPerSecond)
    {
        var mergedTimelineSeconds = 0d;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var segmentDurationSeconds = Math.Max(0.02, (segment.EndFrame - segment.StartFrame + 1) / framesPerSecond);
            var overlayStartSeconds = mergedTimelineSeconds;
            var overlayEndSeconds = mergedTimelineSeconds + segmentDurationSeconds;
            mergedTimelineSeconds = overlayEndSeconds;

            if (!HasEventOverlay(segment))
            {
                continue;
            }

            var enable = $"between(t,{ToInvariant(overlayStartSeconds)},{ToInvariant(Math.Max(overlayStartSeconds, overlayEndSeconds - 0.001))})";
            var accentColor = NormalizeColor(segment.AccentColorHex ?? "155DFC");
            var title = EscapeText(segment.Label.ToUpperInvariant());
            var subtitle = EscapeText(BuildSubtitle(segment));
            var details = EscapeText(BuildDetails(segment));
            var counter = EscapeText(string.IsNullOrWhiteSpace(segment.CounterText) ? $"{index + 1}/{segments.Count}" : segment.CounterText!);

            filters.Add(
                $"drawtext=text='{title}':x=36:y=32:fontsize=28:fontcolor=FFFFFF:box=1:boxcolor={accentColor}@0.92:boxborderw=14:enable='{enable}'");

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                filters.Add(
                    $"drawtext=text='{subtitle}':x=36:y=86:fontsize=22:fontcolor=F8FAFC:box=1:boxcolor=101828@0.72:boxborderw=10:enable='{enable}'");
            }

            if (!string.IsNullOrWhiteSpace(details))
            {
                filters.Add(
                    $"drawtext=text='{details}':x=w-tw-36:y=32:fontsize=22:fontcolor=F8FAFC:box=1:boxcolor=101828@0.72:boxborderw=10:enable='{enable}'");
            }

            if (!string.IsNullOrWhiteSpace(counter))
            {
                filters.Add(
                    $"drawtext=text='{counter}':x=w-tw-36:y=82:fontsize=20:fontcolor=FFFFFF:box=1:boxcolor={accentColor}@0.82:boxborderw=10:enable='{enable}'");
            }
        }
    }

    private static bool HasEventOverlay(ClipSegmentDto segment)
    {
        return segment.TagEventId != Guid.Empty
               || !string.IsNullOrWhiteSpace(segment.TeamName)
               || !string.IsNullOrWhiteSpace(segment.Player)
               || !string.IsNullOrWhiteSpace(segment.Period)
               || !string.IsNullOrWhiteSpace(segment.MatchClockText)
               || !string.IsNullOrWhiteSpace(segment.CounterText);
    }

    private static string BuildSubtitle(ClipSegmentDto segment)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(segment.TeamName))
        {
            parts.Add(segment.TeamName!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(segment.Player))
        {
            parts.Add(segment.Player!.Trim());
        }

        return string.Join(" | ", parts);
    }

    private static string BuildDetails(ClipSegmentDto segment)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(segment.Period))
        {
            parts.Add(segment.Period!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(segment.MatchClockText))
        {
            parts.Add(segment.MatchClockText!.Trim());
        }

        return string.Join(" | ", parts);
    }

    private static string EscapeText(string input)
    {
        return input
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal);
    }

    private static string ToInvariant(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
