using TransGo.Core.Diarization;
using TransGo.Core.Transcription;

namespace TransGo.Application;

/// <summary>
/// Coordinates a transcription engine and optional speaker-attribution
/// engine for one live captioning session.
/// </summary>
public sealed class CaptionSession
{
    private readonly ITranscriptionEngine
        _transcriptionEngine;

    private readonly IDiarizationEngine?
        _diarizationEngine;

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
    }
}
