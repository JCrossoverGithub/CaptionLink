using System;
using System.Threading;
using System.Threading.Tasks;

namespace CaptionLink.Diarization.Nemotron;

/// <summary>
/// Starts and owns the local Nemotron diarization service
/// used by the diarization engine.
/// </summary>
public interface INemotronServiceLauncher
    : IAsyncDisposable
{
    Task<NemotronServiceHealth> EnsureReadyAsync(
        CancellationToken cancellationToken = default);
}
