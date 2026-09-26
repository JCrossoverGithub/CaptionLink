using System;
using System.Threading;
using System.Threading.Tasks;

namespace CaptionLink.Diarization.Sortformer;

/// <summary>
/// Starts and owns the local Sortformer diarization service
/// used by the diarization engine.
/// </summary>
public interface ISortformerServiceLauncher
    : IAsyncDisposable
{
    Task<SortformerServiceHealth> EnsureReadyAsync(
        CancellationToken cancellationToken = default);
}
