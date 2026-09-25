using System;
using System.Windows;
using System.Windows.Input;

namespace TransGo.Windows;

public partial class CaptionOverlayWindow : Window
{
    public CaptionOverlayWindow()
    {
        InitializeComponent();

        Loaded += CaptionOverlayWindow_Loaded;
    }

    public void SetCaption(
        string? speakerLabel,
        string text,
        bool isFinal)
    {
        bool hasSpeakerLabel =
            !string.IsNullOrWhiteSpace(
                speakerLabel);

        SpeakerLabelText.Text =
            hasSpeakerLabel
                ? speakerLabel!.Trim()
                : string.Empty;

        SpeakerLabelText.Visibility =
            hasSpeakerLabel
                ? Visibility.Visible
                : Visibility.Collapsed;

        CaptionText.Text =
            string.IsNullOrWhiteSpace(text)
                ? "Listening for speech..."
                : text;

        /*
         * Interim captions appear slightly dimmer because
         * the recognition engine may replace them.
         */
        CaptionPanel.Opacity =
            isFinal ? 1.0 : 0.82;
    }

    public void ResetCaption()
    {
        SpeakerLabelText.Text =
            string.Empty;

        SpeakerLabelText.Visibility =
            Visibility.Collapsed;

        CaptionText.Text =
            "Listening for speech...";

        CaptionPanel.Opacity = 1.0;
    }

    private void CaptionOverlayWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Left =
            SystemParameters.WorkArea.Left +
            (SystemParameters.WorkArea.Width - ActualWidth) / 2;

        Top =
            SystemParameters.WorkArea.Bottom -
            ActualHeight -
            40;
    }

    private void OverlayBorder_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton ==
            MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosed(
        EventArgs e)
    {
        Loaded -= CaptionOverlayWindow_Loaded;

        base.OnClosed(e);
    }
}