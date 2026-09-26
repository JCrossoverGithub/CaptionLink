using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CaptionLink.Audio.Processing;
using CaptionLink.Audio.Windows;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Transcription;
using CaptionLink.Core.Runtime;
using CaptionLink.Speech.Google;
using CaptionLink.Speech.Sherpa;
using CaptionLink.Speech.Parakeet;
using CaptionLink.Speech.Remote;
using CaptionLink.Core.Diarization;
using CaptionLink.Diarization.Simulated;
using CaptionLink.Diarization.Sortformer;
using CaptionLink.Diarization.Nemotron;

namespace CaptionLink.Windows;

public partial class MainWindow : Window
{
    private const int TranscriptionSampleRate = 48000;

    private readonly IAudioCaptureEngine _captureEngine;
    private readonly IAudioFrameNormalizer _audioNormalizer;
    private readonly CaptionOverlayWindow _captionOverlay;

    private readonly List<AudioOutputDevice> _audioDevices = new();

    private ITranscriptionEngine? _transcriptionEngine;
    private IDiarizationEngine? _diarizationEngine;

    private string _finalTranscript = string.Empty;

    private readonly List<TranscriptDisplayEntry>
        _finalTranscriptEntries = new();

    private bool _followLiveTranscript = true;

    private const double
        LiveTranscriptBottomTolerance = 32.0;

    private static readonly Brush[] TranscriptSpeakerBrushes =
    [
        new SolidColorBrush(Color.FromRgb(125, 211, 252)),
        new SolidColorBrush(Color.FromRgb(252, 211, 77)),
        new SolidColorBrush(Color.FromRgb(134, 239, 172)),
        new SolidColorBrush(Color.FromRgb(196, 181, 253)),
        new SolidColorBrush(Color.FromRgb(253, 164, 175)),
        new SolidColorBrush(Color.FromRgb(253, 186, 116)),
        new SolidColorBrush(Color.FromRgb(94, 234, 212)),
        new SolidColorBrush(Color.FromRgb(147, 197, 253)),
    ];

    private static readonly Brush
        TranscriptOverlapSeparatorBrush =
            new SolidColorBrush(
                Color.FromRgb(170, 182, 202));

    private static readonly ILocalServiceRuntime
        LocalServiceRuntime =
            new WslLocalServiceRuntime();

    private readonly ParakeetServiceLauncher
        _parakeetPreloader =
            new(LocalServiceRuntime);

    private readonly CancellationTokenSource
        _windowCancellation = new();

    private int _transcriptionDisconnectHandled;

    private readonly ConcurrentDictionary<
    string,
    SpeakerActivity> _speakerActivities = new();

    private readonly SpeakerOverlapDetector
        _overlapDetector = new();

    private readonly Pcm16AudioRingBuffer
        _overlapAudioBuffer =
            new(
                sampleRate: 16_000,
                capacity: TimeSpan.FromSeconds(30));

    private int _diarizationFailureHandled;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _captionOverlay =
            new CaptionOverlayWindow();

        _captureEngine =
            new WasapiLoopbackCaptureEngine();

        _audioNormalizer =
            new Pcm16MonoFrameNormalizer();

        _captureEngine.AudioFrameAvailable +=
            CaptureEngine_AudioFrameAvailable;

        _captureEngine.MetricsUpdated +=
            CaptureEngine_MetricsUpdated;

        _captureEngine.CaptureStopped +=
            CaptureEngine_CaptureStopped;

        _audioNormalizer.ChunkAvailable +=
            AudioNormalizer_ChunkAvailable;

        _overlapDetector.RegionUpdated +=
            OverlapDetector_RegionUpdated;

        LoadOutputDevices();
        UpdateProviderDependentControls();
    }

    private int _externalSpeakerAttributionSelectionIndex;
    private bool _wasMultitalkerProviderSelected;

    private string GetSelectedProviderId()
    {
        if (
            TranscriptionProviderComboBox.SelectedItem
                is not ComboBoxItem selectedItem)
        {
            return string.Empty;
        }

        return selectedItem.Tag?
            .ToString()?
            .Trim()
            .ToLowerInvariant()
            ?? string.Empty;
    }

    private bool IsMultitalkerProviderSelected()
    {
        return string.Equals(
            GetSelectedProviderId(),
            "multitalker-parakeet",
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsStandardParakeetProviderSelected()
    {
        return string.Equals(
            GetSelectedProviderId(),
            "parakeet",
            StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateProviderDependentControls()
    {
        /*
         * The provider SelectionChanged event can fire during
         * InitializeComponent before all named controls exist.
         */
        if (
            TranscriptionProviderComboBox is null
            || ParakeetProfileComboBox is null
            || SpeakerAttributionComboBox is null
            || SpeakerAttributionHintText is null)
        {
            return;
        }

        bool isMultitalker =
            IsMultitalkerProviderSelected();

        if (
            isMultitalker
            && !_wasMultitalkerProviderSelected)
        {
            /*
             * Remember the user's external diarization choice.
             * Multitalker already includes Nemotron internally.
             */
            _externalSpeakerAttributionSelectionIndex =
                Math.Max(
                    0,
                    SpeakerAttributionComboBox.SelectedIndex);

            SpeakerAttributionComboBox.SelectedIndex = 0;
        }
        else if (
            !isMultitalker
            && _wasMultitalkerProviderSelected
            && _externalSpeakerAttributionSelectionIndex
                < SpeakerAttributionComboBox.Items.Count)
        {
            SpeakerAttributionComboBox.SelectedIndex =
                _externalSpeakerAttributionSelectionIndex;
        }

        _wasMultitalkerProviderSelected =
            isMultitalker;

        bool configurationEnabled =
            TranscriptionProviderComboBox.IsEnabled;

        ParakeetProfileComboBox.IsEnabled =
            configurationEnabled
            && IsStandardParakeetProviderSelected();

        SpeakerAttributionComboBox.IsEnabled =
            configurationEnabled
            && !isMultitalker;

        SpeakerAttributionHintText.Text =
            isMultitalker
                ? "Included automatically: Multitalker uses Nemotron 3 for speaker detection."
                : "Optional speaker detection for standard transcription providers.";
    }

    private void
        TranscriptionProviderComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
    {
        UpdateProviderDependentControls();
    }

    private IDiarizationEngine?
        CreateSelectedDiarizationEngine()
    {
        /*
         * Multitalker performs speaker detection internally.
         * Never start a second external diarization engine.
         */
        if (IsMultitalkerProviderSelected())
        {
            return null;
        }

        if (
            SpeakerAttributionComboBox.SelectedItem
                is not ComboBoxItem selectedItem)
        {
            return null;
        }

        string mode =
            selectedItem.Tag?
                .ToString()?
                .Trim()
                .ToLowerInvariant()
            ?? "off";

        return mode switch
        {
            "simulated" =>
                new SimulatedDiarizationEngine(),

            "sortformer" =>
                new SortformerDiarizationEngine(
                    new SortformerServiceLauncher(LocalServiceRuntime)),

            "nemotron" =>
                new NemotronDiarizationEngine(
                    new NemotronServiceLauncher(LocalServiceRuntime)),

            _ => null,
        };
    }

    private bool IsSimulatedDiarizationSelected()
    {
        return
            SpeakerAttributionComboBox.SelectedItem
                is ComboBoxItem selectedItem
            &&
            string.Equals(
                selectedItem.Tag?.ToString(),
                "simulated",
                StringComparison.OrdinalIgnoreCase);
    }

    private ParakeetStreamingProfile
        GetSelectedParakeetProfile()
    {
        if (
            ParakeetProfileComboBox.SelectedItem
            is ComboBoxItem selectedItem
            &&
            string.Equals(
                selectedItem.Tag?.ToString(),
                "responsive",
                StringComparison.OrdinalIgnoreCase))
        {
            return ParakeetStreamingProfile.Responsive;
        }

        return ParakeetStreamingProfile.Accurate;
    }

    private async void
        ParakeetProfileComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        ParakeetStreamingProfile profile =
            GetSelectedParakeetProfile();

        StatusText.Text =
            $"Preparing {profile.ToDisplayName()}...";

        ParakeetProfileComboBox.IsEnabled = false;

        StartButton.IsEnabled = false;

        try
        {
            ParakeetServiceHealth health =
                await _parakeetPreloader.EnsureReadyAsync(
                    profile,
                    _windowCancellation.Token);

            StatusText.Text =
                "Local captions ready — " +
                profile.ToDisplayName();

            Debug.WriteLine(
                "Parakeet profile loaded. " +
                $"Profile: {health.Profile}. " +
                $"GPU: {health.Gpu}");
        }
        catch (OperationCanceledException)
            when (_windowCancellation
                .IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Failed to change Parakeet profile: " +
                exception);

            StatusText.Text =
                "Could not change local caption quality";
        }
        finally
        {
            if (
                IsLoaded &&
                !_captureEngine.IsCapturing)
            {
                ParakeetProfileComboBox.IsEnabled =
                    true;

                StartButton.IsEnabled =
                    _audioDevices.Count > 0;
            }
        }
    }

    private async void MainWindow_Loaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await PrepareParakeetAsync(
            _windowCancellation.Token);
    }

    private async Task PrepareParakeetAsync(
        CancellationToken cancellationToken)
    {
        const string preparingMessage =
            "Preparing local captions...";

        StatusText.Text =
            preparingMessage;

        try
        {
            ParakeetServiceHealth health =
                await _parakeetPreloader
                    .EnsureReadyAsync(
                        GetSelectedParakeetProfile(),
                        cancellationToken);

            /*
             * Do not overwrite a newer status message if the user
             * already clicked Start Listening during warm-up.
             */
            if (StatusText.Text ==
                preparingMessage)
            {
                StatusText.Text =
                    "Local captions ready";

                if (!string.IsNullOrWhiteSpace(
                        health.Gpu))
                {
                    StatusText.Text +=
                        $" — {health.Gpu}";
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // The window closed while Parakeet was loading.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Parakeet background preparation failed: " +
                exception);

            /*
             * Starting transcription will retry automatically,
             * so a preload failure does not disable the provider.
             */
            if (StatusText.Text ==
                preparingMessage)
            {
                StatusText.Text =
                    "Local captions will start when needed";
            }
        }
    }

    private async void MainWindow_Closed(
        object? sender,
        EventArgs eventArgs)
    {
        _windowCancellation.Cancel();

        await _parakeetPreloader.DisposeAsync();

        _windowCancellation.Dispose();
    }
    private string GetSelectedProviderName()
    {
        if (TranscriptionProviderComboBox.SelectedItem
            is not ComboBoxItem selectedItem)
        {
            throw new InvalidOperationException(
                "Select a transcription provider.");
        }

        return selectedItem.Content?.ToString()
            ?? throw new InvalidOperationException(
                "The selected provider has no name.");
    }

    private ITranscriptionEngine CreateSelectedTranscriptionEngine()
    {
        if (
            TranscriptionProviderComboBox.SelectedItem
            is not ComboBoxItem selectedItem)
        {
            throw new InvalidOperationException(
                "Select a transcription provider.");
        }

        string providerId =
            selectedItem.Tag?.ToString() ??
            string.Empty;

        return providerId switch
        {
            "sherpa-streaming" =>
                new SherpaStreamingTranscriptionEngine(),

            "hybrid" =>
                new HybridTranscriptionEngine(),

            "whisper-turbo" =>
                new WhisperTurboTranscriptionEngine(),

            "parakeet" =>
                new ParakeetStreamingTranscriptionEngine(
                    GetSelectedParakeetProfile(),
                    new ParakeetServiceLauncher(LocalServiceRuntime)),

            "multitalker-parakeet" =>
                new MultitalkerParakeetTranscriptionEngine(
                    new MultitalkerParakeetServiceLauncher(LocalServiceRuntime)),

            "remote" =>
                new RemoteTranscriptionEngine(),

            "google" =>
                new GoogleStreamingTranscriptionEngine(),

            _ =>
                throw new InvalidOperationException(
                    $"Unknown transcription provider: {providerId}")
        };
    }

    private void LoadOutputDevices()
    {
        string? previouslySelectedId =
            (OutputDeviceComboBox.SelectedItem
                as AudioOutputDevice)?.Id;

        OutputDeviceComboBox.ItemsSource = null;
        _audioDevices.Clear();

        try
        {
            IReadOnlyList<AudioOutputDevice> devices =
                _captureEngine.GetOutputDevices();

            _audioDevices.AddRange(devices);

            OutputDeviceComboBox.ItemsSource =
                _audioDevices;

            AudioOutputDevice? selectedDevice =
                _audioDevices.FirstOrDefault(
                    device =>
                        device.Id == previouslySelectedId)
                ?? _audioDevices.FirstOrDefault(
                    device => device.IsDefault)
                ?? _audioDevices.FirstOrDefault();

            OutputDeviceComboBox.SelectedItem =
                selectedDevice;

            bool devicesAvailable =
                selectedDevice is not null;

            StartButton.IsEnabled =
                devicesAvailable;

            StatusText.Text = devicesAvailable
                ? "Select a device and start listening"
                : "No active audio output devices found";
        }
        catch (Exception exception)
        {
            StartButton.IsEnabled = false;

            StatusText.Text =
                "Could not load audio devices";

            MessageBox.Show(
                "TransGo could not list the available " +
                $"audio devices.\n\n{exception.Message}",
                "Audio device error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshDevicesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureEngine.IsCapturing)
        {
            return;
        }

        LoadOutputDevices();
    }

    private async void StartButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureEngine.IsCapturing ||
            _transcriptionEngine?.IsRunning == true)
        {
            return;
        }

        if (OutputDeviceComboBox.SelectedItem
            is not AudioOutputDevice selectedDevice)
        {
            MessageBox.Show(
                "Select an audio output device first.",
                "No device selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        /*
         * Clean up an engine left behind by an earlier failed
         * startup before creating a new provider instance.
         */
        if (_transcriptionEngine is not null)
        {
            await DisposeTranscriptionEngineAsync();
        }

        if (_diarizationEngine is not null)
        {
            await DisposeDiarizationEngineAsync();
        }

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;

        OutputDeviceComboBox.IsEnabled = false;
        RefreshDevicesButton.IsEnabled = false;
        TranscriptionProviderComboBox.IsEnabled = false;
        ParakeetProfileComboBox.IsEnabled = false;
        SpeakerAttributionComboBox.IsEnabled = false;

        string providerName =
            GetSelectedProviderName();

        StatusText.Text =
            providerName switch
            {
                "Local — Parakeet GPU" =>
                    $"Preparing " +
                    $"{GetSelectedParakeetProfile().ToDisplayName()}...",

                "Local — Multitalker Parakeet" =>
                    "Preparing Multitalker Parakeet + Nemotron 3…",

                "Remote — TransGo GPU Gateway" =>
                    "Connecting to TransGo GPU gateway…",

                "Google Cloud" =>
                    "Connecting to Google Speech-to-Text…",

                _ =>
                    $"Loading {providerName}…"
            };

        ITranscriptionEngine engine =
            CreateSelectedTranscriptionEngine();

        _transcriptionEngine = engine;

        engine.ResultReceived +=
            TranscriptionEngine_ResultReceived;

        if (engine is ISpeakerActivitySource
            speakerActivitySource)
        {
            speakerActivitySource.ActivityReceived +=
                DiarizationEngine_ActivityReceived;
        }

        IDiarizationEngine? diarizationEngine =
            CreateSelectedDiarizationEngine();

        _diarizationEngine =
            diarizationEngine;

        if (diarizationEngine is not null)
        {
            diarizationEngine.ActivityReceived +=
                DiarizationEngine_ActivityReceived;
        }

        try
        {
            _audioNormalizer.Reset();

            _speakerActivities.Clear();
            UpdateActiveSpeakersIndicator();

            _overlapDetector.Reset();
            _overlapAudioBuffer.Reset();

            Interlocked.Exchange(
                ref _diarizationFailureHandled,
                0);

            _finalTranscript =
                string.Empty;

            _finalTranscriptEntries.Clear();

            _followLiveTranscript = true;
            JumpToLiveButton.Visibility =
                Visibility.Collapsed;

            TranscriptText.Inlines.Clear();
            TranscriptText.Inlines.Add(
                new Run("Listening for speech…"));

            _captionOverlay.ResetCaption();

            if (!_captionOverlay.IsVisible)
            {
                _captionOverlay.Show();
            }

            NormalizationText.Text =
                "Normalized chunks: 0";

            AudioLevelMeter.Value = 0;

            LevelText.Text =
                "Audio level: 0%";

            BytesText.Text =
                "Captured: 0 bytes";

            var configuration =
                new TranscriptionConfiguration(
                    LanguageCode: "en-US",
                    SampleRate: TranscriptionSampleRate,
                    EnableInterimResults: true);

            Interlocked.Exchange(
                ref _transcriptionDisconnectHandled,
                0);

            /*
             * Start the selected provider before capture so
             * the first captured audio is not discarded.
             */
            await engine.StartAsync(
                configuration);

            bool speakerAttributionUnavailable =
                false;

            if (diarizationEngine is not null)
            {
                try
                {
                    var diarizationConfiguration =
                        new DiarizationConfiguration(
                            SampleRate:
                                TranscriptionSampleRate,

                            MaximumSpeakers:
                                diarizationEngine
                                    is NemotronDiarizationEngine
                                        ? 8
                                        : 4);

                    await diarizationEngine.StartAsync(
                        diarizationConfiguration);
                }
                catch (Exception exception)
                {
                    /*
                     * Speaker attribution is optional. If it cannot
                     * start, ordinary captions must continue.
                     */
                    speakerAttributionUnavailable =
                        true;

                    Debug.WriteLine(
                        "Speaker attribution startup failed: " +
                        exception);

                    await DisposeDiarizationEngineAsync();
                }
            }

            _captureEngine.Start(
                selectedDevice.Id);

            StatusText.Text =
                speakerAttributionUnavailable
                    ? $"Listening with {providerName} · " +
                      "speaker attribution unavailable"
                    : _diarizationEngine?.IsRunning == true
                        ? $"Listening with {providerName} · " +
                          "speaker attribution enabled"
                        : $"Listening with {providerName}: " +
                          selectedDevice.Name;

            StopButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            try
            {
                if (_captureEngine.IsCapturing)
                {
                    _captureEngine.Stop();
                }
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine(
                    "Capture startup cleanup failed: " +
                    cleanupException);
            }

            try
            {
                await DisposeDiarizationEngineAsync();
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine(
                    "Speaker attribution startup cleanup failed: " +
                    cleanupException);
            }

            try
            {
                await DisposeTranscriptionEngineAsync();
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine(
                    "Transcription startup cleanup failed: " +
                    cleanupException);
            }

            RestoreStoppedControls();

            StatusText.Text =
                "Could not start live transcription";

            MessageBox.Show(
                "TransGo could not start live transcription." +
                $"\n\n{exception.Message}",
                "Transcription startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_captureEngine.IsCapturing)
        {
            return;
        }

        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping…";

        /*
         * CaptureStopped will shut down the selected
         * transcription provider after audio production ends.
         */
        _captureEngine.Stop();
    }

    private void CaptureEngine_AudioFrameAvailable(
        object? sender,
        AudioFrameEventArgs e)
    {
        try
        {
            _audioNormalizer.Process(
                e.Frame);
        }
        catch (Exception exception)
        {
            /*
             * Never allow normalization failures to escape
             * into the real-time NAudio callback.
             */
            Debug.WriteLine(
                $"Audio normalization failed: {exception}");
        }
    }

    private async void AudioNormalizer_ChunkAvailable(
        object? sender,
        TranscriptionAudioChunkEventArgs e)
    {
        TranscriptionAudioChunk chunk =
            e.Chunk;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            NormalizationText.Text =
                $"Normalized chunks: {chunk.Sequence:N0} · " +
                $"Last: {chunk.ByteCount:N0} bytes · " +
                $"{chunk.Duration.TotalMilliseconds:0} ms";
        }));

        try
        {
            _overlapAudioBuffer.Append(
                chunk);
        }
        catch (Exception exception)
        {
            /*
             * Overlap buffering is diagnostic in this milestone.
             * It must never interrupt ordinary captions.
             */
            Debug.WriteLine(
                "Buffering overlap audio failed: " +
                exception);
        }

        ITranscriptionEngine? engine =
            _transcriptionEngine;

        if (engine is null ||
            !engine.IsRunning)
        {
            return;
        }

        try
        {
            await engine.SendAsync(
                chunk);
        }

        catch (InvalidOperationException exception)
            when (
                exception.Message.Contains(
                    "not connected",
                    StringComparison.OrdinalIgnoreCase))
        {
            /*
             * Only handle the first disconnect. Otherwise every
             * 100 ms audio chunk produces another exception.
             */
            if (
                Interlocked.Exchange(
                    ref _transcriptionDisconnectHandled,
                    1)
                != 0)
            {
                return;
            }

            Debug.WriteLine(
                "The transcription service disconnected: " +
                exception);

            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    StatusText.Text =
                        "Transcription service disconnected";

                    StopButton.IsEnabled = false;

                    if (_captureEngine.IsCapturing)
                    {
                        _captureEngine.Stop();
                    }
                }));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Sending transcription audio failed: " +
                exception);
        }

        IDiarizationEngine? diarizationEngine =
    _diarizationEngine;

        if (
            diarizationEngine is null ||
            !diarizationEngine.IsRunning)
        {
            return;
        }

        try
        {
            await diarizationEngine.SendAsync(
                chunk);
        }
        catch (Exception exception)
        {
            /*
             * Diarization failures must not stop ordinary
             * transcription or audio capture.
             */
            if (
                Interlocked.Exchange(
                    ref _diarizationFailureHandled,
                    1)
                != 0)
            {
                return;
            }

            Debug.WriteLine(
                "Sending audio to speaker attribution failed: " +
                exception);

            try
            {
                await DisposeDiarizationEngineAsync();
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine(
                    "Speaker attribution cleanup failed: " +
                    cleanupException);
            }

            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    StatusText.Text =
                        "Speaker attribution unavailable; " +
                        "captions will continue";
                }));
        }
    }

    private static string GetOverlayCaption(
        string text,
        int maximumWords = 18)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmedText =
            text.Trim();

        /*
         * Prefer the newest sentence or unfinished sentence.
         * This prevents earlier completed sentences from
         * remaining in the floating caption.
         */
        int searchIndex =
            trimmedText.Length - 1;

        while (
            searchIndex >= 0 &&
            (char.IsWhiteSpace(
                 trimmedText[searchIndex]) ||
             trimmedText[searchIndex] is
                 '.' or '!' or '?'))
        {
            searchIndex--;
        }

        int previousSentenceBoundary = -1;

        for (
            int index = searchIndex;
            index >= 0;
            index--)
        {
            if (trimmedText[index] is
                '.' or '!' or '?')
            {
                previousSentenceBoundary =
                    index;

                break;
            }
        }

        string currentSentence =
            previousSentenceBoundary >= 0
                ? trimmedText[
                    (previousSentenceBoundary + 1)..]
                    .Trim()
                : trimmedText;

        string[] words =
            currentSentence.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        if (words.Length <= maximumWords)
        {
            return currentSentence;
        }

        return string.Join(
            ' ',
            words[^maximumWords..]);
    }

    private void DiarizationEngine_ActivityReceived(
        object? sender,
        SpeakerActivityEventArgs e)
    {
        SpeakerActivity activity =
            e.Activity;

        _speakerActivities.AddOrUpdate(
            activity.ActivityId,
            activity,
            (_, existingActivity) =>
                activity.Sequence >=
                existingActivity.Sequence
                    ? activity
                    : existingActivity);

        try
        {
            _overlapDetector.Process(
                activity);
        }
        catch (Exception exception)
        {
            /*
             * Detection is intentionally isolated from speaker
             * attribution and visible caption rendering.
             */
            Debug.WriteLine(
                "Processing speaker overlap failed: " +
                exception);
        }

        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(
                new Action(
                    UpdateActiveSpeakersIndicator));
        }

        if (activity.IsFinal)
        {
            Debug.WriteLine(
                "Speaker activity finalized: " +
                $"{activity.SpeakerId}, " +
                $"{activity.StartTime.TotalSeconds:0.0}s–" +
                $"{activity.EndTime.TotalSeconds:0.0}s");
        }
    }

    private void UpdateActiveSpeakersIndicator()
    {
        string[] activeSpeakerIds =
            _speakerActivities
                .Values
                .Where(
                    activity =>
                        !activity.IsFinal)
                .Select(
                    activity =>
                        activity.SpeakerId)
                .Where(
                    speakerId =>
                        !string.IsNullOrWhiteSpace(
                            speakerId))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    speakerId =>
                        speakerId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        ActiveSpeakersText.Inlines.Clear();

        if (activeSpeakerIds.Length == 0)
        {
            ActiveSpeakersText.Visibility =
                Visibility.Collapsed;

            return;
        }

        for (
            int index = 0;
            index < activeSpeakerIds.Length;
            index++)
        {
            if (index > 0)
            {
                ActiveSpeakersText.Inlines.Add(
                    new Run("    "));
            }

            string speakerLabel =
                FormatSpeakerLabel(
                    activeSpeakerIds[index]);

            Brush speakerBrush =
                GetTranscriptSpeakerBrush(
                    speakerLabel);

            ActiveSpeakersText.Inlines.Add(
                new Run("\u25CF ")
                {
                    Foreground = speakerBrush,
                });

            ActiveSpeakersText.Inlines.Add(
                new Run(
                    $"{speakerLabel} speaking")
                {
                    Foreground = speakerBrush,
                    FontWeight =
                        FontWeights.SemiBold,
                });
        }

        ActiveSpeakersText.Visibility =
            Visibility.Visible;
    }

    private void OverlapDetector_RegionUpdated(
        object? sender,
        OverlapRegionEventArgs e)
    {
        OverlapRegion region =
            e.Region;

        bool audioWindowAvailable = false;
        int audioWindowBytes = 0;

        if (
            region.IsFinal
            &&
            _overlapAudioBuffer.TryRead(
                region.AudioStartTime,
                region.AudioEndTime,
                out Pcm16AudioWindow? audioWindow))
        {
            audioWindowAvailable = true;
            audioWindowBytes =
                audioWindow!.Data.Length;
        }

        string diagnosticJson =
            JsonSerializer.Serialize(
                new
                {
                    event_name =
                        "overlap_region",

                    region_id =
                        region.RegionId,

                    revision =
                        region.Revision,

                    start_seconds =
                        region.StartTime.TotalSeconds,

                    end_seconds =
                        region.EndTime.TotalSeconds,

                    overlap_seconds =
                        region.OverlapDuration.TotalSeconds,

                    speaker_ids =
                        region.ActiveSpeakerIds,

                    confidence =
                        region.DetectionConfidence,

                    is_final =
                        region.IsFinal,

                    speaker_limit_exceeded =
                        region.ExceedsSupportedSpeakerCount,

                    audio_start_seconds =
                        region.AudioStartTime.TotalSeconds,

                    audio_end_seconds =
                        region.AudioEndTime.TotalSeconds,

                    audio_window_available =
                        audioWindowAvailable,

                    audio_window_bytes =
                        audioWindowBytes,
                });

        Debug.WriteLine(
            diagnosticJson);
    }

    private void TranscriptionEngine_ResultReceived(
        object? sender,
        TranscriptResultEventArgs e)
    {
        TranscriptResult result =
            e.Result;

        /*
         * Some transcription providers, such as Multitalker
         * Parakeet, already own speaker attribution. Preserve
         * that identity instead of replacing it with a second
         * diarization decision.
         */
        if (string.IsNullOrWhiteSpace(
                result.SpeakerId))
        {
            SpeakerAttributionDecision
                attributionDecision =
                    SpeakerAttributionResolver.Resolve(
                        result,
                        _speakerActivities.Values);

            if (
                !string.IsNullOrWhiteSpace(
                    attributionDecision
                        .CompositeSpeakerId))
            {
                result = result with
                {
                    SpeakerId =
                        attributionDecision
                            .CompositeSpeakerId,
                };
            }

            if (result.IsFinal)
            {
                LogSpeakerAttributionDecision(
                    result,
                    attributionDecision);
            }
        }

        string displayText =
            GetTranscriptDisplayText(result);

        string? speakerLabel =
            string.IsNullOrWhiteSpace(
                result.SpeakerId)
                ? null
                : FormatSpeakerLabel(
                    result.SpeakerId);

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            TranscriptDisplayEntry currentEntry =
                new(
                    Text: result.Text,
                    SpeakerLabel: speakerLabel);

            if (result.IsFinal)
            {
                if (!string.IsNullOrWhiteSpace(
                        _finalTranscript))
                {
                    _finalTranscript +=
                        result.SpeakerId is null
                            ? " "
                            : Environment.NewLine;
                }

                _finalTranscript +=
                    displayText;

                _finalTranscriptEntries.Add(
                    currentEntry);

                /*
                 * Finalized entries remain in the transcript
                 * history. Interim text is rendered separately
                 * so recognition revisions can replace it.
                 */
                RenderTranscript();
            }
            else
            {
                RenderTranscript(
                    currentEntry);
            }

            /*
             * The overlay receives only the newest caption,
             * never the entire accumulated transcript.
             */
            _captionOverlay.SetCaption(
                speakerLabel,
                GetOverlayCaption(result.Text),
                result.IsFinal);
        }));
    }

    private void RenderTranscript(
        TranscriptDisplayEntry? interimEntry = null)
    {
        TranscriptText.Inlines.Clear();

        bool hasPreviousEntry = false;

        foreach (
            TranscriptDisplayEntry entry
            in _finalTranscriptEntries)
        {
            AddTranscriptEntry(
                entry,
                hasPreviousEntry);

            hasPreviousEntry = true;
        }

        if (interimEntry is not null)
        {
            AddTranscriptEntry(
                interimEntry,
                hasPreviousEntry);
        }

        if (
            _finalTranscriptEntries.Count == 0
            && interimEntry is null)
        {
            TranscriptText.Inlines.Add(
                new Run("Listening for speech…"));
        }

        ScrollTranscriptToLiveIfNeeded();
    }

    private void TranscriptScrollViewer_ScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (Math.Abs(e.ExtentHeightChange) > 0.01)
        {
            UpdateJumpToLiveVisibility();
            return;
        }

        if (Math.Abs(e.VerticalChange) <= 0.01)
        {
            return;
        }

        double distanceFromBottom =
            e.ExtentHeight
            - e.ViewportHeight
            - e.VerticalOffset;

        _followLiveTranscript =
            distanceFromBottom
                <= LiveTranscriptBottomTolerance;

        UpdateJumpToLiveVisibility();
    }

    private void JumpToLiveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _followLiveTranscript = true;

        TranscriptScrollViewer.ScrollToEnd();

        UpdateJumpToLiveVisibility();
    }

    private void ScrollTranscriptToLiveIfNeeded()
    {
        if (!_followLiveTranscript)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!_followLiveTranscript)
                {
                    return;
                }

                TranscriptScrollViewer.ScrollToEnd();
                UpdateJumpToLiveVisibility();
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateJumpToLiveVisibility()
    {
        JumpToLiveButton.Visibility =
            _followLiveTranscript
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void AddTranscriptEntry(
        TranscriptDisplayEntry entry,
        bool hasPreviousEntry)
    {
        bool hasSpeaker =
            !string.IsNullOrWhiteSpace(
                entry.SpeakerLabel);

        if (hasPreviousEntry)
        {
            TranscriptText.Inlines.Add(
                hasSpeaker
                    ? new LineBreak()
                    : new Run(" "));
        }

        if (hasSpeaker)
        {
            AddTranscriptSpeakerLabel(
                entry.SpeakerLabel!);

            TranscriptText.Inlines.Add(
                new Run(": ")
                {
                    Foreground = Brushes.White,
                });
        }

        TranscriptText.Inlines.Add(
            new Run(entry.Text)
            {
                Foreground = Brushes.White,
            });
    }

    private void AddTranscriptSpeakerLabel(
        string speakerLabel)
    {
        string[] labels =
            speakerLabel.Split(
                " + ",
                StringSplitOptions
                    .RemoveEmptyEntries |
                StringSplitOptions
                    .TrimEntries);

        for (
            int index = 0;
            index < labels.Length;
            index++)
        {
            if (index > 0)
            {
                TranscriptText.Inlines.Add(
                    new Run(" + ")
                    {
                        Foreground =
                            TranscriptOverlapSeparatorBrush,
                        FontWeight =
                            FontWeights.SemiBold,
                    });
            }

            TranscriptText.Inlines.Add(
                new Run(labels[index])
                {
                    Foreground =
                        GetTranscriptSpeakerBrush(
                            labels[index]),
                    FontWeight =
                        FontWeights.SemiBold,
                });
        }
    }

    private static Brush GetTranscriptSpeakerBrush(
        string speakerLabel)
    {
        const string prefix =
            "Speaker ";

        if (
            speakerLabel.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase)
            &&
            int.TryParse(
                speakerLabel[prefix.Length..],
                out int speakerNumber)
            &&
            speakerNumber > 0)
        {
            int paletteIndex =
                (speakerNumber - 1)
                % TranscriptSpeakerBrushes.Length;

            return TranscriptSpeakerBrushes[
                paletteIndex];
        }

        return TranscriptOverlapSeparatorBrush;
    }

    private sealed record TranscriptDisplayEntry(
        string Text,
        string? SpeakerLabel);

    private static string GetTranscriptDisplayText(
    TranscriptResult result)
    {
        if (string.IsNullOrWhiteSpace(
                result.SpeakerId))
        {
            return result.Text;
        }

        string speakerLabel =
            FormatSpeakerLabel(
                result.SpeakerId);

        return $"{speakerLabel}: {result.Text}";
    }

    private static string FormatSpeakerLabel(
        string speakerId)
    {
        string[] speakerIds =
            speakerId.Split(
                '+',
                StringSplitOptions
                    .RemoveEmptyEntries |
                StringSplitOptions
                    .TrimEntries);

        return string.Join(
            " + ",
            speakerIds.Select(
                FormatSingleSpeakerLabel));
    }

    private static string
        FormatSingleSpeakerLabel(
            string speakerId)
    {
        const string internalPrefix =
            "speaker-";

        if (
            speakerId.StartsWith(
                internalPrefix,
                StringComparison.OrdinalIgnoreCase)
            &&
            int.TryParse(
                speakerId[
                    internalPrefix.Length..],
                out int speakerNumber)
            &&
            speakerNumber > 0)
        {
            return $"Speaker {speakerNumber}";
        }

        return speakerId;
    }

    private static void
        LogSpeakerAttributionDecision(
            TranscriptResult result,
            SpeakerAttributionDecision decision)
    {
        if (
            result.ResultStartTime is not
                TimeSpan resultStart
            ||
            result.ResultEndTime is not
                TimeSpan resultEnd)
        {
            return;
        }

        string selectedSpeakers =
            decision.CompositeSpeakerId ??
            "none";

        string overlapDetails =
            decision.Overlaps.Count == 0
                ? "no diarization overlap"
                : string.Join(
                    "; ",
                    decision.Overlaps.Select(
                        overlap =>
                            $"{overlap.SpeakerId}: " +
                            $"total " +
                            $"{overlap.OverlapDuration.TotalSeconds:0.00}s " +
                            $"({overlap.SegmentCoverage:P0}), " +
                            $"concurrent " +
                            $"{overlap.ConcurrentWithPrimaryDuration.TotalSeconds:0.00}s"));

        string loggedText =
            result.Text.Length <= 160
                ? result.Text
                : result.Text[..160] +
                  "...";

        Debug.WriteLine(
            "Speaker attribution decision: " +
            $"{resultStart.TotalSeconds:0.00}s-" +
            $"{resultEnd.TotalSeconds:0.00}s; " +
            $"selected={selectedSpeakers}; " +
            $"overlapping={decision.IsOverlapping}; " +
            $"{overlapDetails}; " +
            $"text=\"{loggedText}\"");
    }
    private void CaptureEngine_MetricsUpdated(
        object? sender,
        AudioCaptureMetricsEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            AudioLevelMeter.Value =
                e.LevelPercent;

            LevelText.Text =
                $"Audio level: {e.LevelPercent:0}%";

            BytesText.Text =
                $"Captured: {e.CapturedBytes:N0} bytes";
        }));
    }

    private async void CaptureEngine_CaptureStopped(
        object? sender,
        AudioCaptureStoppedEventArgs e)
    {
        Exception? transcriptionError = null;
        Exception? diarizationError = null;

        try
        {
            /*
             * Finalize speaker activity before transcription
             * publishes any final buffered result.
             */
            await DisposeDiarizationEngineAsync();
        }
        catch (Exception exception)
        {
            diarizationError =
                exception;

            Debug.WriteLine(
                "Stopping speaker attribution failed: " +
                exception);
        }

        try
        {
            await DisposeTranscriptionEngineAsync();
        }
        catch (Exception exception)
        {
            transcriptionError =
                exception;

            Debug.WriteLine(
                $"Stopping transcription failed: " +
                exception);
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreStoppedControls();

            if (e.Error is not null)
            {
                StatusText.Text =
                    "Capture stopped because of an error";

                MessageBox.Show(
                    e.Error.Message,
                    "Audio capture stopped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            if (diarizationError is not null)
            {
                Debug.WriteLine(
                    "Speaker attribution stopped with an error: " +
                    diarizationError);
            }

            if (transcriptionError is not null)
            {
                StatusText.Text =
                    "Transcription stopped with an error";

                MessageBox.Show(
                    transcriptionError.Message,
                    "Transcription error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            StatusText.Text = "Stopped";
        }));
    }

    private async Task
        DisposeDiarizationEngineAsync()
    {
        IDiarizationEngine? engine =
            _diarizationEngine;

        if (engine is null)
        {
            return;
        }

        /*
         * Remove the shared reference immediately so new
         * audio chunks cannot be queued during shutdown.
         */
        _diarizationEngine = null;

        try
        {
            if (engine.IsRunning)
            {
                /*
                 * Keep the event subscribed during StopAsync
                 * so the engine can finalize its last interval.
                 */
                await engine.StopAsync();
            }
        }
        finally
        {
            engine.ActivityReceived -=
                DiarizationEngine_ActivityReceived;

            try
            {
                await engine.DisposeAsync();
            }
            finally
            {
                CompleteOverlapDetection();
            }
        }
    }

    private void CompleteOverlapDetection()
    {
        TimeSpan sessionEndTime =
            _overlapAudioBuffer
                .AvailableEndTime
            ?? TimeSpan.Zero;

        _overlapDetector.Complete(
            sessionEndTime);

        OverlapDetectionMetrics detectionMetrics =
            _overlapDetector.GetMetrics();

        Pcm16AudioRingBufferMetrics bufferMetrics =
            _overlapAudioBuffer.GetMetrics();

        string summaryJson =
            JsonSerializer.Serialize(
                new
                {
                    event_name =
                        "overlap_session_summary",

                    activity_updates =
                        detectionMetrics
                            .ActivityUpdatesReceived,

                    stale_activity_updates =
                        detectionMetrics
                            .StaleActivityUpdatesIgnored,

                    regions_started =
                        detectionMetrics
                            .RegionsStarted,

                    regions_finalized =
                        detectionMetrics
                            .RegionsFinalized,

                    unsupported_regions =
                        detectionMetrics
                            .UnsupportedSpeakerRegions,

                    final_overlap_seconds =
                        detectionMetrics
                            .TotalFinalOverlapDuration
                            .TotalSeconds,

                    buffered_seconds =
                        bufferMetrics
                            .StoredDuration
                            .TotalSeconds,

                    buffer_discontinuities =
                        bufferMetrics
                            .Discontinuities,

                    overwritten_audio_bytes =
                        bufferMetrics
                            .OverwrittenOutputBytes,
                });

        Debug.WriteLine(
            summaryJson);
    }

    private async Task
        DisposeTranscriptionEngineAsync()
    {
        ITranscriptionEngine? engine =
            _transcriptionEngine;

        if (engine is null)
        {
            return;
        }

        /*
         * Remove the shared reference immediately so new
         * audio chunks cannot be queued during shutdown.
         */
        _transcriptionEngine = null;

        try
        {
            if (engine.IsRunning)
            {
                /*
                 * Keep the result event subscribed during
                 * StopAsync so the engine can publish its
                 * final buffered transcript.
                 */
                await engine.StopAsync();
            }
        }
        finally
        {
            if (engine is ISpeakerActivitySource
                speakerActivitySource)
            {
                speakerActivitySource.ActivityReceived -=
                    DiarizationEngine_ActivityReceived;
            }

            engine.ResultReceived -=
                TranscriptionEngine_ResultReceived;

            await engine.DisposeAsync();
        }
    }

    private void RestoreStoppedControls()
    {
        AudioLevelMeter.Value = 0;

        LevelText.Text =
            "Audio level: 0%";

        StartButton.IsEnabled =
            _audioDevices.Count > 0;

        StopButton.IsEnabled = false;

        OutputDeviceComboBox.IsEnabled = true;
        RefreshDevicesButton.IsEnabled = true;
        TranscriptionProviderComboBox.IsEnabled = true;

        UpdateProviderDependentControls();
    }

    protected override async void OnClosed(
        EventArgs e)
    {
        _captureEngine.AudioFrameAvailable -=
            CaptureEngine_AudioFrameAvailable;

        _captureEngine.MetricsUpdated -=
            CaptureEngine_MetricsUpdated;

        _captureEngine.CaptureStopped -=
            CaptureEngine_CaptureStopped;

        _audioNormalizer.ChunkAvailable -=
            AudioNormalizer_ChunkAvailable;

        try
        {
            if (_captureEngine.IsCapturing)
            {
                _captureEngine.Stop();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Audio capture cleanup failed: " +
                exception);
        }

        _audioNormalizer.Dispose();
        _captureEngine.Dispose();
        _captionOverlay.Close();

        base.OnClosed(e);

        try
        {
            await DisposeDiarizationEngineAsync();
            await DisposeTranscriptionEngineAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Transcription or speaker attribution " +
                $"cleanup failed: {exception}");
        }

        _overlapDetector.RegionUpdated -=
            OverlapDetector_RegionUpdated;
    }
}
