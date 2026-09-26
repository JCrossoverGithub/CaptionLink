namespace CaptionLink.OverlapBenchmark;

internal sealed class AudioProgressGate
    : IDisposable
{
    private static readonly TimeSpan ProgressTimeout =
        TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _signal =
        new(0);

    private long _acknowledgedChunks;

    public void Observe(long chunksReceived)
    {
        if (chunksReceived <= 0)
        {
            return;
        }

        Volatile.Write(
            ref _acknowledgedChunks,
            chunksReceived);

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
                    "The diarization engine did not acknowledge " +
                    $"audio chunk {requiredChunks} within " +
                    $"{ProgressTimeout.TotalSeconds:0} seconds.");
            }
        }
    }

    public void Dispose() =>
        _signal.Dispose();
}
