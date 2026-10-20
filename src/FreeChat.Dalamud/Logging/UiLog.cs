namespace FreeChat.Dalamud.Logging;

public enum UiLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record UiLogEntry(DateTimeOffset At, UiLogLevel Level, string Message);

public sealed class UiLog
{
    private const int Capacity = 500;

    private readonly object gate = new();
    private readonly Queue<UiLogEntry> entries = new();

    public event Action? Changed;

    public void Add(UiLogLevel level, string message)
    {
        lock (this.gate)
        {
            this.entries.Enqueue(new UiLogEntry(DateTimeOffset.Now, level, message));
            while (this.entries.Count > Capacity)
                this.entries.Dequeue();
        }

        this.Changed?.Invoke();
    }

    public IReadOnlyList<UiLogEntry> Snapshot()
    {
        lock (this.gate)
            return this.entries.ToArray();
    }

    public void Clear()
    {
        lock (this.gate)
            this.entries.Clear();

        this.Changed?.Invoke();
    }
}
