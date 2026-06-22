using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using VideoAnalysis.App.Services;
using VideoAnalysis.Core.Dtos;

namespace VideoAnalysis.App.Media;

public sealed class BroadcastDvrService : IDisposable
{
    private const int SegmentSeconds = 1;
    private const int RangeAvailabilityWaitSeconds = SegmentSeconds + 3;
    private const double PreviewKeyFrameSeconds = 0.5d;
    private const int PreviewUdpFifoPackets = 16384;
    private const int PreviewUdpBufferBytes = 1048576;
    private const double FallbackFramesPerSecond = 30d;
    private const double MacFramePreviewFramesPerSecond = 30d;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _ffmpegPath;
    private Process? _process;
    private Task<string>? _stderrTask;
    private Task<string>? _stdoutTask;
    private string? _projectFolderPath;
    private string? _sessionFolderPath;
    private string? _segmentFolderPath;
    private string? _previewFolderPath;
    private string? _previewFramePath;
    private string? _indexPath;
    private string? _previewSource;
    private DateTimeOffset _startedAtUtc;
    private bool _disposed;

    public BroadcastDvrService(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
    }

    public bool IsRunning => _process is { HasExited: false };
    public bool HasExportableArchive => HasExportableArchiveCore();
    public string PreviewSource => _previewSource ?? string.Empty;
    public DateTimeOffset StartedAtUtc => _startedAtUtc;

    public bool TryAttachLatestSession(string projectFolderPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(projectFolderPath))
        {
            return false;
        }

        var resolvedProjectFolderPath = Path.GetFullPath(projectFolderPath);
        var dvrRootPath = Path.Combine(resolvedProjectFolderPath, "media", "live-dvr");
        if (!Directory.Exists(dvrRootPath))
        {
            return false;
        }

        foreach (var sessionFolderPath in Directory
                     .GetDirectories(dvrRootPath, "session-*")
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var segmentFolderPath = Path.Combine(sessionFolderPath, "segments");
            if (!Directory.Exists(segmentFolderPath)
                || !Directory.EnumerateFiles(segmentFolderPath, "segment-*.ts").Any(IsFinalizedSegmentFile))
            {
                continue;
            }

            _projectFolderPath = resolvedProjectFolderPath;
            _sessionFolderPath = sessionFolderPath;
            _segmentFolderPath = segmentFolderPath;
            _previewFolderPath = Path.Combine(sessionFolderPath, "preview");
            _previewFramePath = Path.Combine(_previewFolderPath, "preview.jpg");
            _indexPath = Path.Combine(sessionFolderPath, "live-dvr-index.json");
            _startedAtUtc = ReadIndexStartedAtUtc(_indexPath)
                ?? ParseSessionStartedAtUtc(sessionFolderPath)
                ?? DateTimeOffset.UtcNow;
            return true;
        }

        return false;
    }

    public long GetAvailableFrameLimit()
    {
        var index = BuildIndex();
        return index.Segments.Count == 0
            ? 1
            : Math.Max(1, index.Segments.Max(segment => segment.EndFrame) + 1);
    }

    public async Task<BroadcastDvrSession> StartAsync(
        string projectFolderPath,
        string? cameraName,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BroadcastDvrService));
        }

        if (IsRunning
            && !string.IsNullOrWhiteSpace(_previewSource)
            && !string.IsNullOrWhiteSpace(_indexPath))
        {
            return new BroadcastDvrSession(_previewSource, _indexPath, _startedAtUtc, FallbackFramesPerSecond, _previewFramePath ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(projectFolderPath))
        {
            throw new ArgumentException("Project folder path is required.", nameof(projectFolderPath));
        }

        var resolvedProjectFolderPath = Path.GetFullPath(projectFolderPath);
        var canResumeSession =
            string.Equals(_projectFolderPath, resolvedProjectFolderPath, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_sessionFolderPath)
            && !string.IsNullOrWhiteSpace(_segmentFolderPath)
            && !string.IsNullOrWhiteSpace(_indexPath)
            && Directory.Exists(_segmentFolderPath)
            && _startedAtUtc != default;

        var resolvedCameraName = ResolveCameraName(cameraName);
        if (!canResumeSession)
        {
            _projectFolderPath = resolvedProjectFolderPath;
            _startedAtUtc = DateTimeOffset.UtcNow;
            var dvrRootPath = Path.Combine(resolvedProjectFolderPath, "media", "live-dvr");
            _sessionFolderPath = Path.Combine(dvrRootPath, $"session-{_startedAtUtc:yyyyMMdd-HHmmss}");
            _segmentFolderPath = Path.Combine(_sessionFolderPath, "segments");
            _previewFolderPath = Path.Combine(_sessionFolderPath, "preview");
            _previewFramePath = Path.Combine(_previewFolderPath, "preview.jpg");
            _indexPath = Path.Combine(_sessionFolderPath, "live-dvr-index.json");
        }

        var segmentFolderPath = _segmentFolderPath
            ?? throw new InvalidOperationException("Live DVR segment folder was not initialized.");
        var indexPath = _indexPath
            ?? throw new InvalidOperationException("Live DVR index path was not initialized.");

        Directory.CreateDirectory(segmentFolderPath);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ResetPreviewFolder(_previewFolderPath ?? Path.Combine(_sessionFolderPath!, "preview"));
        }

        var previewPort = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0 : FindAvailableUdpPort();
        var previewFramePath = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? _previewFramePath ?? Path.Combine(_previewFolderPath ?? Path.Combine(_sessionFolderPath!, "preview"), "preview.jpg")
            : string.Empty;
        _previewSource = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new Uri(previewFramePath).AbsoluteUri
            : BuildPreviewSource(previewPort);
        var segmentPattern = Path.Combine(segmentFolderPath, "segment-%06d.ts");
        var segmentStartNumber = GetNextSegmentIndex(segmentFolderPath);

        var ffmpegPath = ResolveFfmpegPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = BuildDvrArguments(resolvedCameraName, segmentPattern, segmentStartNumber, previewPort, previewFramePath),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start ffmpeg live DVR.");
        _stderrTask = _process.StandardError.ReadToEndAsync(cancellationToken);
        _stdoutTask = _process.StandardOutput.ReadToEndAsync(cancellationToken);

        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        if (_process.HasExited)
        {
            var details = await ReadProcessOutputAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"ffmpeg live DVR exited immediately. FFmpeg: {ffmpegPath}. {details}");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await WaitForPreviewFrameAsync(previewFramePath, cancellationToken).ConfigureAwait(false);
        }

        await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
        return new BroadcastDvrSession(_previewSource, indexPath, _startedAtUtc, FallbackFramesPerSecond, previewFramePath);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;

        if (process is null)
        {
            await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void DetachSession()
    {
        if (IsRunning)
        {
            return;
        }

        _projectFolderPath = null;
        _sessionFolderPath = null;
        _segmentFolderPath = null;
        _previewFolderPath = null;
        _previewFramePath = null;
        _indexPath = null;
        _previewSource = null;
        _startedAtUtc = default;
        _stderrTask = null;
        _stdoutTask = null;
    }

    public async Task<string> ExportRangeAsync(
        long startFrame,
        long endFrame,
        double framesPerSecond,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var safeFramesPerSecond = NormalizeFramesPerSecond(framesPerSecond);
        var safeStartFrame = Math.Max(0, Math.Min(startFrame, endFrame));
        var safeEndFrame = Math.Max(safeStartFrame, Math.Max(startFrame, endFrame));
        var index = await WaitForRangeAsync(safeStartFrame, safeEndFrame, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), "video-analysis", "live-dvr-range", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await ExportRangeFromIndexAsync(
                    index,
                    safeStartFrame,
                    safeEndFrame,
                    safeFramesPerSecond,
                    outputPath,
                    tempRoot,
                    cancellationToken)
                .ConfigureAwait(false);

            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task<string> ExportAvailableRangeAsync(
        long startFrame,
        long requestedEndFrame,
        double framesPerSecond,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var safeFramesPerSecond = NormalizeFramesPerSecond(framesPerSecond);
        var safeStartFrame = Math.Max(0, Math.Min(startFrame, requestedEndFrame));
        var safeRequestedEndFrame = Math.Max(safeStartFrame, Math.Max(startFrame, requestedEndFrame));
        var (index, availableEndFrame) = await WaitForAvailableRangeAsync(
                safeStartFrame,
                safeRequestedEndFrame,
                cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), "video-analysis", "live-dvr-range", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await ExportRangeFromIndexAsync(
                    index,
                    safeStartFrame,
                    Math.Min(safeRequestedEndFrame, availableEndFrame),
                    safeFramesPerSecond,
                    outputPath,
                    tempRoot,
                    cancellationToken)
                .ConfigureAwait(false);

            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task<BroadcastDvrPreparedExport> PrepareExportSourceAsync(
        IReadOnlyList<ClipSegmentDto> segments,
        double framesPerSecond,
        string outputFolderPath,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Нет сегментов для экспорта.");
        }

        var safeFramesPerSecond = NormalizeFramesPerSecond(framesPerSecond);
        Directory.CreateDirectory(outputFolderPath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "video-analysis", "live-dvr-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var sourceParts = new List<string>(segments.Count);
            var remappedSegments = new List<ClipSegmentDto>(segments.Count);
            var cursorFrame = 0L;
            var index = await WaitForRangeAsync(
                    segments.Min(segment => Math.Max(0, segment.StartFrame)),
                    segments.Max(segment => Math.Max(segment.StartFrame, segment.EndFrame)),
                    cancellationToken)
                .ConfigureAwait(false);
            var availableStartFrame = index.Segments.Count == 0
                ? 0
                : index.Segments.Min(segment => segment.StartFrame);
            var availableEndFrame = index.Segments.Count == 0
                ? 0
                : index.Segments.Max(segment => segment.EndFrame);

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var requestedStartFrame = Math.Max(0, Math.Min(segment.StartFrame, segment.EndFrame));
                var requestedEndFrame = Math.Max(requestedStartFrame, Math.Max(segment.StartFrame, segment.EndFrame));
                var startFrame = Math.Max(requestedStartFrame, availableStartFrame);
                var endFrame = Math.Min(requestedEndFrame, availableEndFrame);
                if (endFrame < startFrame)
                {
                    continue;
                }

                var partPath = Path.Combine(tempRoot, $"clip_{i:D4}.mp4");

                await ExportRangeFromIndexAsync(
                        index,
                        startFrame,
                        endFrame,
                        safeFramesPerSecond,
                        partPath,
                        Path.Combine(tempRoot, $"range_{i:D4}"),
                        cancellationToken)
                    .ConfigureAwait(false);

                sourceParts.Add(partPath);
                var frameCount = endFrame - startFrame + 1;
                remappedSegments.Add(segment with
                {
                    StartFrame = cursorFrame,
                    EndFrame = cursorFrame + frameCount - 1
                });
                cursorFrame += frameCount;
            }

            if (sourceParts.Count == 0)
            {
                throw new InvalidOperationException("Для выбранных моментов еще нет доступных live-DVR кадров.");
            }

            var sourcePath = Path.Combine(outputFolderPath, $"live-dvr-export-source-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.mp4");
            await ConcatFilesAsync(sourceParts, sourcePath, tempRoot, cancellationToken).ConfigureAwait(false);
            return new BroadcastDvrPreparedExport(sourcePath, remappedSegments);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task<BroadcastDvrIndex> SaveIndexAsync(CancellationToken cancellationToken)
    {
        var index = BuildIndex();
        if (!string.IsNullOrWhiteSpace(_indexPath))
        {
            await File.WriteAllTextAsync(
                    _indexPath,
                    JsonSerializer.Serialize(index, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return index;
    }

    private bool HasExportableArchiveCore()
    {
        return !string.IsNullOrWhiteSpace(_segmentFolderPath)
            && Directory.Exists(_segmentFolderPath)
            && Directory.EnumerateFiles(_segmentFolderPath, "segment-*.ts").Any(IsFinalizedSegmentFile);
    }

    public void Dispose()
    {
        ShutdownFast();
    }

    public void ShutdownFast()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _ = SaveIndexAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        _process?.Dispose();
        _process = null;
    }

    private BroadcastDvrIndex BuildIndex()
    {
        var segments = new List<BroadcastDvrSegment>();
        if (string.IsNullOrWhiteSpace(_segmentFolderPath) || !Directory.Exists(_segmentFolderPath))
        {
            return new BroadcastDvrIndex(
                _startedAtUtc == default ? DateTimeOffset.UtcNow : _startedAtUtc,
                FallbackFramesPerSecond,
                SegmentSeconds,
                []);
        }

        var files = Directory
            .GetFiles(_segmentFolderPath, "segment-*.ts")
            .Where(IsFinalizedSegmentFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var framesPerSegment = (long)Math.Round(FallbackFramesPerSecond * SegmentSeconds);
        for (var index = 0; index < files.Count; index++)
        {
            var startFrame = index * framesPerSegment;
            var endFrame = startFrame + framesPerSegment - 1;
            var startUtc = _startedAtUtc + TimeSpan.FromSeconds(index * SegmentSeconds);
            var endUtc = startUtc + TimeSpan.FromSeconds(SegmentSeconds);
            segments.Add(new BroadcastDvrSegment(files[index], startFrame, endFrame, startUtc, endUtc));
        }

        return new BroadcastDvrIndex(_startedAtUtc, FallbackFramesPerSecond, SegmentSeconds, segments);
    }

    private static DateTimeOffset? ReadIndexStartedAtUtc(string? indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            var index = JsonSerializer.Deserialize<BroadcastDvrIndex>(File.ReadAllText(indexPath), JsonOptions);
            return index?.StartedAtUtc;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseSessionStartedAtUtc(string sessionFolderPath)
    {
        var folderName = Path.GetFileName(sessionFolderPath);
        const string prefix = "session-";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DateTime.TryParseExact(
            folderName[prefix.Length..],
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var startedAt)
            ? new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc))
            : null;
    }

    private async Task<BroadcastDvrIndex> WaitForRangeAsync(
        long startFrame,
        long endFrame,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(RangeAvailabilityWaitSeconds);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            if (CoversRange(index.Segments, startFrame, endFrame))
            {
                return index;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException("Запрошенный фрагмент еще не записан в live-DVR. Подождите несколько секунд и повторите экспорт.");
    }

    private async Task<(BroadcastDvrIndex Index, long AvailableEndFrame)> WaitForAvailableRangeAsync(
        long startFrame,
        long requestedEndFrame,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(RangeAvailabilityWaitSeconds);
        BroadcastDvrIndex? bestIndex = null;
        var bestEndFrame = -1L;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
            if (TryGetContiguousEndFrame(index.Segments, startFrame, out var availableEndFrame))
            {
                bestIndex = index;
                bestEndFrame = availableEndFrame;
                if (availableEndFrame >= requestedEndFrame)
                {
                    return (index, requestedEndFrame);
                }
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        if (bestIndex is not null && bestEndFrame >= startFrame)
        {
            return (bestIndex, bestEndFrame);
        }

        throw new InvalidOperationException("Фрагмент еще не попал в live-DVR. Подождите пару секунд и попробуйте остановить запись снова.");
    }

    private async Task ExportRangeFromIndexAsync(
        BroadcastDvrIndex index,
        long startFrame,
        long endFrame,
        double framesPerSecond,
        string outputPath,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempRoot);
        var dvrSegments = index.Segments
            .Where(segment => segment.EndFrame >= startFrame && segment.StartFrame <= endFrame)
            .OrderBy(segment => segment.StartFrame)
            .ToList();

        if (dvrSegments.Count == 0)
        {
            throw new InvalidOperationException("Для выбранного диапазона не хватает записанных live-DVR сегментов.");
        }

        var effectiveStartFrame = Math.Max(startFrame, dvrSegments[0].StartFrame);
        var effectiveEndFrame = Math.Min(endFrame, dvrSegments[^1].EndFrame);
        if (effectiveEndFrame < effectiveStartFrame || !CoversRange(dvrSegments, effectiveStartFrame, effectiveEndFrame))
        {
            throw new InvalidOperationException("Для выбранного диапазона не хватает записанных live-DVR сегментов.");
        }

        var mergedTransportStreamPath = Path.Combine(tempRoot, "merged.ts");
        await ConcatFilesAsync(
                dvrSegments.Select(segment => segment.FilePath).ToList(),
                mergedTransportStreamPath,
                tempRoot,
                cancellationToken)
            .ConfigureAwait(false);

        var startSeconds = (effectiveStartFrame - dvrSegments[0].StartFrame) / framesPerSecond;
        var durationSeconds = Math.Max(0.02, (effectiveEndFrame - effectiveStartFrame + 1) / framesPerSecond);
        var args = string.Join(' ',
            "-y",
            $"-ss {ToInvariant(startSeconds)}",
            $"-i {Quote(mergedTransportStreamPath)}",
            $"-t {ToInvariant(durationSeconds)}",
            "-map 0:v:0",
            "-an",
            "-c:v libx264 -preset veryfast -crf 20",
            "-pix_fmt yuv420p",
            "-movflags +faststart",
            Quote(outputPath));

        await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConcatFilesAsync(
        IReadOnlyList<string> sourcePaths,
        string outputPath,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        var listPath = Path.Combine(tempRoot, $"concat-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(
                listPath,
                string.Join(Environment.NewLine, sourcePaths.Select(path => $"file '{EscapeConcatPath(path)}'")),
                cancellationToken)
            .ConfigureAwait(false);

        await RunFfmpegAsync(
                $"-y -f concat -safe 0 -i {Quote(listPath)} -c copy {Quote(outputPath)}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunFfmpegAsync(string arguments, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("Unable to start ffmpeg process.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stdErr = await stdErrTask.ConfigureAwait(false);
            var stdOut = await stdOutTask.ConfigureAwait(false);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}. {stdErr}{stdOut}");
        }
    }

    private static bool CoversRange(IReadOnlyList<BroadcastDvrSegment> segments, long startFrame, long endFrame)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        var orderedSegments = segments.OrderBy(segment => segment.StartFrame).ToList();
        if (orderedSegments[0].StartFrame > startFrame)
        {
            return false;
        }

        var coveredUntil = orderedSegments[0].EndFrame;
        foreach (var segment in orderedSegments.Skip(1))
        {
            if (segment.StartFrame > coveredUntil + 1)
            {
                return false;
            }

            coveredUntil = Math.Max(coveredUntil, segment.EndFrame);
            if (coveredUntil >= endFrame)
            {
                return true;
            }
        }

        return coveredUntil >= endFrame;
    }

    private static bool TryGetContiguousEndFrame(
        IReadOnlyList<BroadcastDvrSegment> segments,
        long startFrame,
        out long endFrame)
    {
        endFrame = -1;
        foreach (var segment in segments.OrderBy(segment => segment.StartFrame))
        {
            if (segment.EndFrame < startFrame)
            {
                continue;
            }

            if (segment.StartFrame > startFrame && endFrame < startFrame)
            {
                return false;
            }

            if (endFrame < startFrame)
            {
                endFrame = segment.EndFrame;
                continue;
            }

            if (segment.StartFrame > endFrame + 1)
            {
                return endFrame >= startFrame;
            }

            endFrame = Math.Max(endFrame, segment.EndFrame);
        }

        return endFrame >= startFrame;
    }

    private static bool IsFinalizedSegmentFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length > 0
                && DateTimeOffset.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMilliseconds(900);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveCameraName(string? cameraName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return string.IsNullOrWhiteSpace(cameraName)
                ? "0"
                : cameraName.Trim();
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new InvalidOperationException("Broadcast DVR from camera is currently available on Windows and macOS only.");
        }

        var resolvedCameraName = string.IsNullOrWhiteSpace(cameraName)
            ? WindowsCameraDeviceEnumerator.GetVideoCaptureDevices().FirstOrDefault()?.Name
            : cameraName.Trim();
        if (string.IsNullOrWhiteSpace(resolvedCameraName))
        {
            throw new InvalidOperationException("Camera was not found.");
        }

        return resolvedCameraName;
    }

    private string ResolveFfmpegPath()
    {
        var probeErrors = new List<string>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateBundledFfmpegCandidates())
        {
            if (TryUseFfmpegCandidate(candidate, seenCandidates, probeErrors, out var bundledCandidate))
            {
                AppLogService.Info($"Using bundled FFmpeg for live-DVR: {bundledCandidate}", "BroadcastDvr");
                return bundledCandidate;
            }
        }

        if (Path.IsPathRooted(_ffmpegPath))
        {
            if (TryUseFfmpegCandidate(_ffmpegPath, seenCandidates, probeErrors, out var rootedCandidate))
            {
                AppLogService.Info($"Using configured FFmpeg for live-DVR: {rootedCandidate}", "BroadcastDvr");
                return rootedCandidate;
            }

            if (!File.Exists(_ffmpegPath))
            {
                probeErrors.Add($"FFmpeg was not found at '{_ffmpegPath}'.");
            }
        }

        foreach (var candidate in EnumerateFfmpegCandidates(_ffmpegPath))
        {
            if (TryUseFfmpegCandidate(candidate, seenCandidates, probeErrors, out var resolvedCandidate))
            {
                AppLogService.Info($"Using discovered FFmpeg for live-DVR: {resolvedCandidate}", "BroadcastDvr");
                return resolvedCandidate;
            }
        }

        var details = probeErrors.Count == 0
            ? $"FFmpeg was not found. Current value: '{_ffmpegPath}'."
            : string.Join(Environment.NewLine, probeErrors);
        AppLogService.Error($"No usable FFmpeg executable was found.{Environment.NewLine}{details}", "BroadcastDvr");
        throw new InvalidOperationException($"No usable FFmpeg executable was found.{Environment.NewLine}{details}");
    }

    private static bool TryUseFfmpegCandidate(
        string candidate,
        ISet<string> seenCandidates,
        ICollection<string> probeErrors,
        out string resolvedCandidate)
    {
        resolvedCandidate = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(candidate);
        if (!seenCandidates.Add(fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        if (IsUsableFfmpeg(fullPath, out var error))
        {
            resolvedCandidate = fullPath;
            return true;
        }

        probeErrors.Add($"FFmpeg at '{fullPath}' is not usable: {error}");
        return false;
    }

    private static bool IsUsableFfmpeg(string ffmpegPath, out string error)
    {
        error = string.Empty;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                error = "process did not start.";
                return false;
            }

            if (!process.WaitForExit(2_000))
            {
                process.Kill(entireProcessTree: true);
                error = "version probe timed out.";
                return false;
            }

            if (process.ExitCode == 0)
            {
                return true;
            }

            var output = $"{process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}".Trim();
            error = string.IsNullOrWhiteSpace(output)
                ? $"version probe exited with code {process.ExitCode}."
                : output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                  ?? $"version probe exited with code {process.ExitCode}.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFfmpegCandidates(string configuredPath)
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(configuredPath) && !Path.IsPathRooted(configuredPath))
        {
            yield return Path.GetFullPath(configuredPath, Directory.GetCurrentDirectory());
            yield return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
        }

        foreach (var candidateName in new[]
        {
            executableName,
            Path.Combine("tools", executableName),
            Path.Combine("tools", "ffmpeg", executableName),
            Path.Combine("tools", "ffmpeg", "bin", executableName),
            Path.Combine("tools", "ffmpeg", "macos-arm64", "unpacked", executableName),
            Path.Combine("tools", "ffmpeg", "macos-x64", "unpacked", executableName)
        })
        {
            yield return Path.Combine(AppContext.BaseDirectory, candidateName);
            yield return Path.Combine(Directory.GetCurrentDirectory(), candidateName);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            yield break;
        }

        foreach (var pathEntry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(pathEntry, executableName);
        }
    }

    private static IEnumerable<string> EnumerateBundledFfmpegCandidates()
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var candidateNames = new[]
        {
            executableName,
            Path.Combine("tools", executableName),
            Path.Combine("tools", "ffmpeg", executableName),
            Path.Combine("tools", "ffmpeg", "bin", executableName),
            Path.Combine("tools", "ffmpeg", "macos-arm64", "unpacked", executableName),
            Path.Combine("tools", "ffmpeg", "macos-x64", "unpacked", executableName)
        };

        foreach (var candidateName in candidateNames)
        {
            yield return Path.Combine(AppContext.BaseDirectory, candidateName);
        }
    }

    private static int GetNextSegmentIndex(string segmentFolderPath)
    {
        if (!Directory.Exists(segmentFolderPath))
        {
            return 0;
        }

        return Directory
            .GetFiles(segmentFolderPath, "segment-*.ts")
            .Select(static path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.StartsWith("segment-", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name["segment-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : -1;
            })
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    private static string BuildDvrArguments(
        string cameraName,
        string segmentPattern,
        int segmentStartNumber,
        int previewPort,
        string previewFramePath)
    {
        var previewTarget = $"udp://127.0.0.1:{previewPort}?pkt_size=1316&buffer_size={PreviewUdpBufferBytes}";
        var segmentForceKeyFrames = $"expr:gte(t,n_forced*{SegmentSeconds})";
        var previewForceKeyFrames = $"expr:gte(t,n_forced*{ToInvariant(PreviewKeyFrameSeconds)})";
        var segmentGop = (int)Math.Round(FallbackFramesPerSecond * SegmentSeconds);
        var previewGop = Math.Max(1, (int)Math.Round(FallbackFramesPerSecond * PreviewKeyFrameSeconds));
        var cameraInputArguments = BuildCameraInputArguments(cameraName);
        var previewOutputArguments = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? BuildMacFramePreviewOutputArguments(previewFramePath)
            : BuildUdpPreviewOutputArguments(previewTarget, previewGop, previewForceKeyFrames);
        var segmentOutputArguments = string.Join(' ',
            "-map 0:v:0",
            "-an",
            "-c:v libx264",
            "-preset ultrafast",
            "-tune zerolatency",
            "-x264-params repeat-headers=1",
            "-crf 20",
            "-pix_fmt yuv420p",
            $"-r {ToInvariant(FallbackFramesPerSecond)}",
            $"-g {segmentGop}",
            $"-keyint_min {segmentGop}",
            "-sc_threshold 0",
            $"-force_key_frames {Quote(segmentForceKeyFrames)}",
            "-f segment",
            $"-segment_time {SegmentSeconds}",
            $"-segment_start_number {Math.Max(0, segmentStartNumber)}",
            "-reset_timestamps 1",
            "-segment_format mpegts",
            Quote(segmentPattern));

        return string.Join(' ',
            "-y",
            "-fflags nobuffer",
            "-probesize 32",
            "-analyzeduration 0",
            cameraInputArguments,
            segmentOutputArguments,
            previewOutputArguments);
    }

    private static string BuildUdpPreviewOutputArguments(string previewTarget, int previewGop, string previewForceKeyFrames)
    {
        return string.Join(' ',
            "-map 0:v:0",
            "-an",
            "-c:v libx264",
            "-preset ultrafast",
            "-tune zerolatency",
            "-x264-params repeat-headers=1",
            "-crf 20",
            "-pix_fmt yuv420p",
            $"-r {ToInvariant(FallbackFramesPerSecond)}",
            $"-g {previewGop}",
            $"-keyint_min {previewGop}",
            "-sc_threshold 0",
            $"-force_key_frames {Quote(previewForceKeyFrames)}",
            "-fflags nobuffer",
            "-muxdelay 0",
            "-muxpreload 0",
            "-flush_packets 1",
            "-mpegts_flags +resend_headers",
            "-f mpegts",
            Quote(previewTarget));
    }

    private static string BuildMacFramePreviewOutputArguments(string previewFramePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || string.IsNullOrWhiteSpace(previewFramePath))
        {
            return string.Empty;
        }

        return string.Join(' ',
            "-map 0:v:0",
            "-an",
            $"-vf fps={ToInvariant(MacFramePreviewFramesPerSecond)},scale=1280:-2",
            "-q:v 4",
            "-update 1",
            "-f image2",
            Quote(previewFramePath));
    }

    private static string BuildCameraInputArguments(string cameraName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var avFoundationSource = $"{cameraName}:none";
            return string.Join(' ',
                "-f avfoundation",
                $"-framerate {ToInvariant(FallbackFramesPerSecond)}",
                "-thread_queue_size 32",
                $"-i {Quote(avFoundationSource)}");
        }

        return string.Join(' ',
            "-f dshow",
            "-rtbufsize 32M",
            "-thread_queue_size 32",
            $"-i {Quote($"video={cameraName}")}");
    }

    private static int FindAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static string BuildPreviewSource(int previewPort)
    {
        return $"udp://127.0.0.1:{previewPort}?fifo_size={PreviewUdpFifoPackets}&overrun_nonfatal=1&buffer_size={PreviewUdpBufferBytes}";
    }

    private static void ResetPreviewFolder(string previewFolderPath)
    {
        if (Directory.Exists(previewFolderPath))
        {
            try
            {
                Directory.Delete(previewFolderPath, recursive: true);
            }
            catch
            {
            }
        }

        Directory.CreateDirectory(previewFolderPath);
    }

    private static async Task WaitForPreviewFrameAsync(string previewFramePath, CancellationToken cancellationToken)
    {
        var timeoutAt = Environment.TickCount64 + 3_000;
        while (!File.Exists(previewFramePath) && Environment.TickCount64 < timeoutAt)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> ReadProcessOutputAsync()
    {
        var stderr = _stderrTask is null ? string.Empty : await _stderrTask.ConfigureAwait(false);
        var stdout = _stdoutTask is null ? string.Empty : await _stdoutTask.ConfigureAwait(false);
        return $"{stderr}{stdout}".Trim();
    }

    private static double NormalizeFramesPerSecond(double framesPerSecond)
    {
        return framesPerSecond > 0.01d ? framesPerSecond : FallbackFramesPerSecond;
    }

    private static string ToInvariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record BroadcastDvrSession(
    string PreviewSource,
    string IndexPath,
    DateTimeOffset StartedAtUtc,
    double FramesPerSecond,
    string PreviewFramePath);

public sealed record BroadcastDvrIndex(
    DateTimeOffset StartedAtUtc,
    double FramesPerSecond,
    int SegmentSeconds,
    IReadOnlyList<BroadcastDvrSegment> Segments);

public sealed record BroadcastDvrSegment(
    string FilePath,
    long StartFrame,
    long EndFrame,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc);

public sealed record BroadcastDvrPreparedExport(
    string SourceVideoPath,
    IReadOnlyList<ClipSegmentDto> Segments);
