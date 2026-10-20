namespace FreeChat.Server;

public sealed class TokenBucket
{
    private readonly object gate = new();
    private readonly double capacity;
    private readonly double refillPerSecond;
    private double tokens;
    private DateTimeOffset lastRefill;

    public TokenBucket(int capacity, double refillPerSecond, IClock clock)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(refillPerSecond));

        this.capacity = capacity;
        this.refillPerSecond = refillPerSecond;
        this.tokens = capacity;
        this.lastRefill = clock.UtcNow;
        this.Clock = clock;
    }

    public IClock Clock { get; }

    public bool TryTake(double cost = 1)
    {
        lock (this.gate)
        {
            var now = this.Clock.UtcNow;
            var elapsed = (now - this.lastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                this.tokens = Math.Min(this.capacity, this.tokens + elapsed * this.refillPerSecond);
                this.lastRefill = now;
            }

            if (this.tokens < cost)
                return false;

            this.tokens -= cost;
            return true;
        }
    }
}
