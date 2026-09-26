using TransGo.Core.Audio;
using TransGo.Core.Diarization;
using TransGo.Core.Transcription;

namespace TransGo.Application;

/// <summary>
/// Coordinates the transcription engine and optional speaker-attribution
/// engine for one live captioning session.
/// </summary>
public sealed class CaptionSession : IAsyncDisposable
{
    private readonly ITranscriptionEngine
        _transcriptionEngine;

    private readonly IDiarizationEngine?
        _diarizationEngine;

    private int _disposed;

    public CaptionSession(
        ITranscriptionEngine transcriptionEngine,
        IDiarizationEngine? diarizationEngine = null)
    {
        ArgumentNullException.ThrowIfNull(
            transcriptionEngine);

        _transcriptionEngine =
            transcriptionEngine;

        _diarizationEngine =
            diarizationEngine;

        _transcriptionEngine.ResultReceived +=
            TranscriptionEngine_ResultReceived;

        if (_transcriptionEngine
            is ISpeakerActivitySource speakerActivitySource)
        {
            speakerActivitySource.ActivityReceived +=
                SpeakerActivitySource_ActivityReceived;
        }

        if (_diarizationEngine is not null)
        {
            _diarizationEngine.ActivityReceived +=
                SpeakerActivitySource_ActivityReceived;
        }
    }

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

    public event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

    public bool IsRunning =>
        _transcriptionEngine.IsRunning;

    public bool IsSpeakerAttributionRunning =>
        _diarizationEngine?.IsRunning == true;

    public async Task StartAsync(
        TranscriptionConfiguration
            transcriptionConfiguration,
        DiarizationConfiguration?
            diarizationConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(
            transcriptionConfiguration);

        if (
            _diarizationEngine is not null
            && diarizationConfiguration is null)
        {
            throw new ArgumentNullException(
                nameof(diarizationConfiguration),
                "A diarization configuration is required " +
                "when a diarization engine is present.");
        }

        try
        {
            await _transcriptionEngine.StartAsync(
                transcriptionConfiguration,
                cancellationToken);

            if (_diarizationEngine is not null)
            {
                await _diarizationEngine.StartAsync(
                    diarizationConfiguration!,
                    cancellationToken);
            }
        }
        catch (Exception startupException)
        {
            try
            {
                /*
                 * Startup is atomic at the session boundary.
                 * Stop anything that became active before the
                 * startup failure was reported.
                 */
                await StopCoreAsync(
                    CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Starting the caption session failed and " +
                    "rollback also failed.",
                    startupException,
                    rollbackException);
            }

            throw;
        }
    }

    public async ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(
            chunk);

        if (_transcriptionEngine.IsRunning)
        {
            await _transcriptionEngine.SendAsync(
                chunk,
                cancellationToken);
        }

        if (_diarizationEngine?.IsRunning == true)
        {
            await _diarizationEngine.SendAsync(
                chunk,
                cancellationToken);
        }
    }

    public Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return StopCoreAsync(
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (
            Interlocked.Exchange(
                ref _disposed,
                1)
            != 0)
        {
            return;
        }

        try
        {
            await StopCoreAsync(
                CancellationToken.None);
        }
        finally
        {
            _transcriptionEngine.ResultReceived -=
                TranscriptionEngine_ResultReceived;

            if (_transcriptionEngine
                is ISpeakerActivitySource
                    speakerActivitySource)
            {
                speakerActivitySource.ActivityReceived -=
                    SpeakerActivitySource_ActivityReceived;
            }

            if (_diarizationEngine is not null)
            {
                _diarizationEngine.ActivityReceived -=
                    SpeakerActivitySource_ActivityReceived;

                try
                {
                    await _diarizationEngine.DisposeAsync();
                }
                finally
                {
                    await _transcriptionEngine.DisposeAsync();
                }
            }
            else
            {
                await _transcriptionEngine.DisposeAsync();
            }
        }
    }

    private async Task StopCoreAsync(
        CancellationToken cancellationToken)
    {
        Exception? diarizationError = null;
        Exception? transcriptionError = null;

        if (_diarizationEngine?.IsRunning == true)
        {
            try
            {
                /*
                 * Finalize speaker activity first so transcription
                 * can use the final activity intervals while it
                 * publishes any buffered final result.
                 */
                await _diarizationEngine.StopAsync(
                    cancellationToken);
            }
            catch (Exception exception)
            {
                diarizationError =
                    exception;
            }
        }

        if (_transcriptionEngine.IsRunning)
        {
            try
            {
                await _transcriptionEngine.StopAsync(
                    cancellationToken);
            }
            catch (Exception exception)
            {
                transcriptionError =
                    exception;
            }
        }

        if (
            diarizationError is not null
            && transcriptionError is not null)
        {
            throw new AggregateException(
                "Stopping the caption session failed.",
                diarizationError,
                transcriptionError);
        }

        if (transcriptionError is not null)
        {
            throw transcriptionError;
        }

        if (diarizationError is not null)
        {
            throw diarizationError;
        }
    }

    private void TranscriptionEngine_ResultReceived(
        object? sender,
        TranscriptResultEventArgs e)
    {
        ResultReceived?.Invoke(
            this,
            e);
    }

    private void SpeakerActivitySource_ActivityReceived(
        object? sender,
        SpeakerActivityEventArgs e)
    {
        ActivityReceived?.Invoke(
            this,
            e);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
