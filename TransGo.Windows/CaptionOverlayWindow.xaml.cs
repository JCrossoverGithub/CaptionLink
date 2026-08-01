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
        string text,
        bool isFinal)
    {
        CaptionText.Text =
            string.IsNullOrWhiteSpace(text)
                ? "Listening for speech..."
                : text;

        /*
         * Interim captions appear slightly dimmer because
         * Google may replace them as recognition improves.
         */
        CaptionText.Opacity =
            isFinal ? 1.0 : 0.82;
    }

    public void ResetCaption()
    {
        CaptionText.Text =
            "Listening for speech...";

        CaptionText.Opacity = 1.0;
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