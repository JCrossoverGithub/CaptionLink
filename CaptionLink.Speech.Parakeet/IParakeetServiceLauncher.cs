using System;
using System.Threading;
using System.Threading.Tasks;

namespace CaptionLink.Speech.Parakeet;

/// <summary>
/// Starts and owns the local Parakeet transcription service
/// used by the streaming transcription engine.
/// </summary>
public interface IParakeetServiceLauncher
    : IAsyncDisposable
{
    Task<ParakeetServiceHealth> EnsureReadyAsync(
        ParakeetStreamingProfile profile,
        CancellationToken cancellationToken = default);
}
