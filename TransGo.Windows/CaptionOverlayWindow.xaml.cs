using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TransGo.Windows;

public partial class CaptionOverlayWindow : Window
{
    private static readonly Brush[] SpeakerBrushes =
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

    private static readonly Brush OverlapSeparatorBrush =
        new SolidColorBrush(
            Color.FromRgb(170, 182, 202));

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

        SpeakerLabelText.Inlines.Clear();

        if (hasSpeakerLabel)
        {
            AddSpeakerLabelInlines(
                speakerLabel!);
        }

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
        SpeakerLabelText.Inlines.Clear();

        SpeakerLabelText.Visibility =
            Visibility.Collapsed;

        CaptionText.Text =
            "Listening for speech...";

        CaptionPanel.Opacity = 1.0;
    }

    private void AddSpeakerLabelInlines(
        string speakerLabel)
    {
        string[] labels =
            speakerLabel.Split(
                " + ",
                StringSplitOptions
                    .RemoveEmptyEntries |
                StringSplitOptions
                    .TrimEntries);

        for (int index = 0; index < labels.Length; index++)
        {
            if (index > 0)
            {
                SpeakerLabelText.Inlines.Add(
                    new Run(" + ")
                    {
                        Foreground =
                            OverlapSeparatorBrush,
                    });
            }

            SpeakerLabelText.Inlines.Add(
                new Run(labels[index])
                {
                    Foreground =
                        GetSpeakerBrush(
                            labels[index]),
                });
        }
    }

    private static Brush GetSpeakerBrush(
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
                (speakerNumber - 1) %
                SpeakerBrushes.Length;

            return SpeakerBrushes[
                paletteIndex];
        }

        return OverlapSeparatorBrush;
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