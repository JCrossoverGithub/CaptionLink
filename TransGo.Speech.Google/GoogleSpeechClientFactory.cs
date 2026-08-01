using GoogleSpeechClient =
    global::Google.Cloud.Speech.V1.SpeechClient;

namespace TransGo.Speech.Google;

internal static class GoogleSpeechClientFactory
{
    public static GoogleSpeechClient Create()
    {
        return GoogleSpeechClient.Create();
    }
}
