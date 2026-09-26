using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;
using CaptionLink.Core.Transcription;

namespace CaptionLink.Application;

/// <summary>
/// Coordinates the transcription engine and optional speaker-attribution
/// engine for one live captioning session.
/// </summary>
public sealed class CaptionSession : IAsyncDisposable
{
    private readonly ITranscriptionEngine
        _transcriptionEngine;

    private IDiarizationEngine?
        _diarizationEngine;

    private int _diarizationFailureHandled;
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

    public event EventHandler<
        SpeakerAttributionUnavailableEventArgs>?
        SpeakerAttributionUnavailable;

    public bool IsRunning =>
        _transcriptionEngine.IsRunning;

    public bool IsSpeakerAttributionRunning =>
        Volatile.Read(ref _diarizationEngine)
            ?.IsRunning == true;

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

        IDiarizationEngine? diarizationEngine =
            Volatile.Read(
                ref _diarizationEngine);

        if (
            diarizationEngine is not null
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
        }
        catch (Exception startupException)
        {
            try
            {
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

        if (diarizationEngine is null)
        {
            return;
        }

        try
        {
            await diarizationEngine.StartAsync(
                diarizationConfiguration!,
                cancellationToken);
        }
        catch (OperationCanceledException cancellationException)
            when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StopCoreAsync(
                    CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Starting the caption session was canceled and " +
                    "rollback also failed.",
                    cancellationException,
                    rollbackException);
            }

            throw;
        }
        catch (Exception exception)
        {
            /*
             * External speaker attribution is optional.
             * Transcription stays active if diarization cannot start.
             */
            await DisableDiarizationAsync(
                diarizationEngine,
                exception);
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

        IDiarizationEngine? diarizationEngine =
            Volatile.Read(
                ref _diarizationEngine);

        if (diarizationEngine?.IsRunning != true)
        {
            return;
        }

        try
        {
            await diarizationEngine.SendAsync(
                chunk,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            /*
             * Losing optional speaker attribution must not stop
             * ordinary transcription.
             */
            await DisableDiarizationAsync(
                diarizationEngine,
                exception);
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

            IDiarizationEngine? diarizationEngine =
                Interlocked.Exchange(
                    ref _diarizationEngine,
                    null);

            if (diarizationEngine is not null)
            {
                diarizationEngine.ActivityReceived -=
                    SpeakerActivitySource_ActivityReceived;

                try
                {
                    await diarizationEngine.DisposeAsync();
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

    private async Task DisableDiarizationAsync(
        IDiarizationEngine failedEngine,
        Exception failure)
    {
        if (
            Interlocked.Exchange(
                ref _diarizationFailureHandled,
                1)
            != 0)
        {
            return;
        }

        IDiarizationEngine? removedEngine =
            Interlocked.CompareExchange(
                ref _diarizationEngine,
                null,
                failedEngine);

        if (!ReferenceEquals(
                removedEngine,
                failedEngine))
        {
            return;
        }

        failedEngine.ActivityReceived -=
            SpeakerActivitySource_ActivityReceived;

        Exception reportedException =
            failure;

        try
        {
            if (failedEngine.IsRunning)
            {
                await failedEngine.StopAsync(
                    CancellationToken.None);
            }
        }
        catch (Exception stopException)
        {
            reportedException =
                new AggregateException(
                    "Speaker attribution failed and could not " +
                    "be stopped cleanly.",
                    reportedException,
                    stopException);
        }

        try
        {
            await failedEngine.DisposeAsync();
        }
        catch (Exception disposeException)
        {
            reportedException =
                new AggregateException(
                    "Speaker attribution failed and could not " +
                    "be disposed cleanly.",
                    reportedException,
                    disposeException);
        }

        SpeakerAttributionUnavailable?.Invoke(
            this,
            new SpeakerAttributionUnavailableEventArgs(
                reportedException));
    }

    private async Task StopCoreAsync(
        CancellationToken cancellationToken)
    {
        Exception? diarizationError = null;
        Exception? transcriptionError = null;

        IDiarizationEngine? diarizationEngine =
            Volatile.Read(
                ref _diarizationEngine);

        if (diarizationEngine?.IsRunning == true)
        {
            try
            {
                /*
                 * Finalize speaker activity first so transcription
                 * can use the final activity intervals while it
                 * publishes any buffered final result.
                 */
                await diarizationEngine.StopAsync(
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
