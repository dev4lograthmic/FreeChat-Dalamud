using System.Collections.Concurrent;
using System.Security.Cryptography;
using FreeChat.Protocol.V1;
using FreeChat.Server;

namespace FreeChat.Dalamud;

public sealed class PairingApprovalService : IPairingApproval
{
    private readonly ConcurrentDictionary<long, PendingApproval> pending = new();
    private long nextId;

    public IReadOnlyList<PendingApproval> Pending => this.pending.Values.OrderBy(x => x.Id).ToArray();

    public Task<bool> ApproveAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref this.nextId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fingerprint = Convert.ToHexString(SHA256.HashData(request.PublicKey));
        var approval = new PendingApproval(id, request, fingerprint, completion);
        this.pending[id] = approval;

        cancellationToken.Register(() =>
        {
            if (this.pending.TryRemove(id, out var removed))
                removed.Completion.TrySetCanceled(cancellationToken);
        });

        return completion.Task;
    }

    public void Resolve(long id, bool approved)
    {
        if (this.pending.TryRemove(id, out var approval))
            approval.Completion.TrySetResult(approved);
    }
}

public sealed class PendingApproval
{
    public PendingApproval(
        long id,
        PairingRequest request,
        string fingerprint,
        TaskCompletionSource<bool> completion)
    {
        this.Id = id;
        this.Request = request;
        this.Fingerprint = fingerprint;
        this.Completion = completion;
    }

    public long Id { get; }
    public PairingRequest Request { get; }
    public string Fingerprint { get; }
    internal TaskCompletionSource<bool> Completion { get; }
}
