using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Core.Abstractions;

public interface IVideoProxyService
{
    Task<VideoProxyResult> EnsureProxyAsync(
        string sourceVideoPath,
        string projectFolderPath,
        CancellationToken cancellationToken);
}
