using FreeChat.Protocol.V1;

namespace FreeChat.Server;

public sealed record ChatBacklogEntry(ChatEvent Event, int Bytes);

public sealed class ChatBacklog
{
    private readonly object gate = new();
    private readonly LinkedList<ChatBacklogEntry> entries = new();
    private int maxMessages;
    private long maxBytes;
    private long bytes;

    public ChatBacklog(int maxMessages = 5000, long maxBytes = 32L * 1024 * 1024)
    {
        this.maxMessages = Math.Max(0, maxMessages);
        this.maxBytes = Math.Max(0, maxBytes);
    }

    public int Count
    {
        get
        {
            lock (this.gate)
                return this.entries.Count;
        }
    }

    public long Bytes
    {
        get
        {
            lock (this.gate)
                return this.bytes;
        }
    }

    public long OldestSequence
    {
        get
        {
            lock (this.gate)
                return this.entries.First?.Value.Event.Sequence ?? 0;
        }
    }

    public long LatestSequence
    {
        get
        {
            lock (this.gate)
                return this.entries.Last?.Value.Event.Sequence ?? 0;
        }
    }

    public void Configure(int messages, long bytesLimit)
    {
        lock (this.gate)
        {
            this.maxMessages = Math.Max(0, messages);
            this.maxBytes = Math.Max(0, bytesLimit);
            this.Trim();
        }
    }

    public void Append(ChatEvent chatEvent)
    {
        var size = chatEvent.CalculateSize();
        lock (this.gate)
        {
            if (this.maxMessages == 0 || this.maxBytes == 0 || size > this.maxBytes)
                return;

            this.entries.AddLast(new ChatBacklogEntry(chatEvent, size));
            this.bytes += size;
            this.Trim();
        }
    }

    public IReadOnlyList<ChatBacklogEntry> Snapshot()
    {
        lock (this.gate)
            return this.entries.ToArray();
    }

    public IReadOnlyList<ChatBacklogEntry> After(long sequence)
    {
        lock (this.gate)
            return this.entries.Where(x => x.Event.Sequence > sequence).ToArray();
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
            this.bytes = 0;
        }
    }

    private void Trim()
    {
        while (this.entries.First is not null
               && (this.entries.Count > this.maxMessages || this.bytes > this.maxBytes))
        {
            this.bytes -= this.entries.First.Value.Bytes;
            this.entries.RemoveFirst();
        }
    }
}
