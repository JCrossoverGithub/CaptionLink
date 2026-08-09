using TransGo.Diarization.Sortformer;

namespace TransGo.OverlapBenchmark;

internal sealed class AudioProgressGate
    : IDisposable
{
    private static readonly TimeSpan ProgressTimeout =
        TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _signal =
        new(0);

    private long _acknowledgedChunks;

    public void Observe(
        object? sender,
        SortformerAudioProgressEventArgs eventArgs)
    {
        Volatile.Write(
            ref _acknowledgedChunks,
            eventArgs.ChunksReceived);

        _signal.Release();
    }

    public async Task WaitForAsync(
        long requiredChunks,
        CancellationToken cancellationToken)
    {
        while (
            Volatile.Read(
                ref _acknowledgedChunks) <
            requiredChunks)
        {
            bool signaled =
                await _signal.WaitAsync(
                    ProgressTimeout,
                    cancellationToken);

            if (!signaled)
            {
                throw new TimeoutException(
                    "Sortformer did not acknowledge " +
                    $"audio chunk {requiredChunks} within " +
                    $"{ProgressTimeout.TotalSeconds:0} seconds.");
            }
        }
    }

    public void Dispose() =>
        _signal.Dispose();
}
