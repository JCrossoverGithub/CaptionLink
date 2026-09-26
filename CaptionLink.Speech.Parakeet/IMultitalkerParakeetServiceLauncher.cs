using System;
using System.Threading;
using System.Threading.Tasks;

namespace CaptionLink.Speech.Parakeet;

/// <summary>
/// Starts and owns the local Multitalker transcription service
/// used by the Multitalker transcription engine.
/// </summary>
public interface IMultitalkerParakeetServiceLauncher
    : IAsyncDisposable
{
    Task EnsureReadyAsync(
        CancellationToken cancellationToken = default);
}
