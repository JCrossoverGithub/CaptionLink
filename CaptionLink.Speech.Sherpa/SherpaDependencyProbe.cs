using SherpaOnnx;

namespace CaptionLink.Speech.Sherpa;

internal static class SherpaDependencyProbe
{
    public static string GetRecognizerTypeName()
    {
        SherpaModelPaths model =
            SherpaModelPaths.CreateDefaultEnglish();

        if (!model.IsInstalled)
        {
            return "Sherpa model not installed";
        }

        return typeof(OnlineRecognizer).FullName
            ?? nameof(OnlineRecognizer);
    }
}