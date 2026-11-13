using System.Threading.RateLimiting;

namespace LeCrocodile.Reviews.Api;

/// <summary>Enforces five accepted requests in every rolling one-second interval.</summary>
public sealed class RollingWindowRateLimiter : RateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly TimeProvider timeProvider;
    private readonly Queue<long> timestamps = new Queue<long>();
    private readonly object gate = new object();
    private long lastAccepted;

    public RollingWindowRateLimiter(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        lastAccepted = timeProvider.GetTimestamp();
    }

    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (gate)
            {
                TimeSpan elapsed = timeProvider.GetElapsedTime(lastAccepted);
                return elapsed >= Window ? elapsed - Window : null;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        lock (gate)
        {
            long now = timeProvider.GetTimestamp();
            while (timestamps.TryPeek(out long oldest) && timeProvider.GetElapsedTime(oldest, now) >= Window)
            {
                timestamps.Dequeue();
            }

            if (permitCount > 5 - timestamps.Count)
            {
                TimeSpan retryAfter = timestamps.Count == 0
                    ? Window : Window - timeProvider.GetElapsedTime(timestamps.Peek(), now);
                return new WindowLease(false, retryAfter);
            }

            for (int index = 0; index < permitCount; index++)
            {
                timestamps.Enqueue(now);
            }

            if (permitCount > 0)
            {
                lastAccepted = now;
            }

            return new WindowLease(true, TimeSpan.Zero);
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AttemptAcquireCore(permitCount));
    }

    private sealed class WindowLease(bool isAcquired, TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => isAcquired;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}
