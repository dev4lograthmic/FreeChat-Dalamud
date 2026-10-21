using System.Collections.Concurrent;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FreeChat.Protocol.V1;
using FreeChat.Server;
using Lumina.Excel.Sheets;
using ChatChannel = FreeChat.Protocol.V1.ChatChannel;

namespace FreeChat.Dalamud.Social;

/// <summary>
/// Reads linkshell names, the friend list, and friend presence from the game.
/// All native pointers stay on the framework thread; callers receive managed
/// snapshots only.
/// </summary>
public sealed class GameSocialData : ISocialDataProvider, IDisposable
{
    private const int LinkshellSlots = 8;
    private const int MaxFriends = 200;
    private const int MaxQueuedRequests = 256;
    private static readonly TimeSpan LinkshellRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FriendCacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PresenceCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private delegate void EndRequestDelegate(nint proxy);
    private delegate void PresenceResponseDelegate(nint proxy, nint response, ulong contentId);

    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;

    private readonly object nameGate = new();
    private readonly string?[] localLinkshells = new string?[LinkshellSlots];
    private readonly string?[] crossLinkshells = new string?[LinkshellSlots];

    private readonly ConcurrentQueue<PendingFriendRequest> friendQueue = new();
    private readonly ConcurrentQueue<PendingPresenceRequest> presenceQueue = new();
    private readonly List<PendingFriendRequest> friendWaiters = new();
    private int queuedFriendRequests;
    private int queuedPresenceRequests;

    private Hook<EndRequestDelegate>? endRequestHook;
    private Hook<PresenceResponseDelegate>? presenceHook;
    private nint endRequestAddress;
    private nint presenceAddress;
    private nint watchedProxy;
    private bool friendRefreshActive;
    private bool friendEndRequested;
    private bool friendEndRequestOffThread;
    private bool presenceActive;
    private bool presenceCompleted;
    private bool presenceResponseOffThread;
    private ushort presenceCode;
    private ulong presenceContentId;
    private DateTimeOffset friendDeadline;
    private DateTimeOffset presenceDeadline;
    private PendingPresenceRequest? activePresence;

    private FriendCache? friendCache;
    private readonly Dictionary<ulong, (PresenceState State, DateTimeOffset At)> presenceCache = new();

    private string? ownerKey;
    private long nextLinkshellRefresh;
    private int clearRequested;
    private bool disposed;

    public GameSocialData(
        Configuration configuration,
        IClientState clientState,
        IPlayerState playerState,
        IDataManager data,
        IFramework framework,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.playerState = playerState;
        this.data = data;
        this.framework = framework;
        this.interop = interop;
        this.log = log;
    }

    public bool FriendListAvailable =>
        this.configuration.EnableSocialApi && this.configuration.EnableFriendListApi;

    public bool FriendPresenceAvailable =>
        this.FriendListAvailable && this.configuration.EnableFriendPresenceApi;

    public string? NameFor(ChatChannel channel)
    {
        var index = channel switch
        {
            >= ChatChannel.Linkshell1 and <= ChatChannel.Linkshell8
                => (int)channel - (int)ChatChannel.Linkshell1,
            >= ChatChannel.CrossLinkshell1 and <= ChatChannel.CrossLinkshell8
                => (int)channel - (int)ChatChannel.CrossLinkshell1,
            _ => -1,
        };
        if (index < 0 || index >= LinkshellSlots)
            return null;

        lock (this.nameGate)
        {
            return channel is >= ChatChannel.Linkshell1 and <= ChatChannel.Linkshell8
                ? this.localLinkshells[index]
                : this.crossLinkshells[index];
        }
    }

    public async Task<FriendListData> GetFriendListAsync(
        SocialRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!this.FriendListAvailable)
            return new FriendListData(PlayerListStatus.Unavailable, Array.Empty<PlayerInfo>(), "Friend list API is disabled.");
        if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded)
            return new FriendListData(PlayerListStatus.NotLoggedIn, Array.Empty<PlayerInfo>(), "Not logged in.");
        if (!this.OwnerMatches(context))
            return new FriendListData(PlayerListStatus.IdentityChanged, Array.Empty<PlayerInfo>(), "Character changed.");

        var pending = new PendingFriendRequest(context);
        if (Interlocked.Increment(ref this.queuedFriendRequests) > MaxQueuedRequests)
        {
            Interlocked.Decrement(ref this.queuedFriendRequests);
            return new FriendListData(PlayerListStatus.Busy, Array.Empty<PlayerInfo>(), "Friend request queue is full.");
        }

        this.friendQueue.Enqueue(pending);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            return await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pending.Cancel();
            return new FriendListData(PlayerListStatus.TimedOut, Array.Empty<PlayerInfo>(), "Friend list request timed out.");
        }
        catch (OperationCanceledException)
        {
            pending.Cancel();
            throw;
        }
    }

    public async Task<FriendPresenceData> GetFriendPresenceAsync(
        ulong contentId,
        SocialRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!this.FriendPresenceAvailable)
            return new FriendPresenceData(PlayerListStatus.Unavailable, PresenceState.Unknown, "Friend presence API is disabled.");
        if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded)
            return new FriendPresenceData(PlayerListStatus.NotLoggedIn, PresenceState.Unknown, "Not logged in.");
        if (!this.OwnerMatches(context))
            return new FriendPresenceData(PlayerListStatus.IdentityChanged, PresenceState.Unknown, "Character changed.");

        var cache = this.friendCache;
        if (cache is null || !cache.ContentIds.Contains(contentId))
            return new FriendPresenceData(PlayerListStatus.Unavailable, PresenceState.Unknown, "Refresh the friend list first.");

        var pending = new PendingPresenceRequest(contentId, context);
        if (Interlocked.Increment(ref this.queuedPresenceRequests) > MaxQueuedRequests)
        {
            Interlocked.Decrement(ref this.queuedPresenceRequests);
            return new FriendPresenceData(PlayerListStatus.Busy, PresenceState.Unknown, "Presence request queue is full.");
        }

        this.presenceQueue.Enqueue(pending);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            return await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pending.Cancel();
            return new FriendPresenceData(PlayerListStatus.TimedOut, PresenceState.Unknown, "Presence request timed out.");
        }
        catch (OperationCanceledException)
        {
            pending.Cancel();
            throw;
        }
    }

    /// <summary>Called from the framework update thread.</summary>
    public void Tick()
    {
        if (this.disposed)
            return;

        if (Volatile.Read(ref this.clearRequested) != 0
            || !this.clientState.IsLoggedIn
            || !this.playerState.IsLoaded)
        {
            this.ClearState(PlayerListStatus.NotLoggedIn);
            return;
        }

        var currentOwner = this.CurrentOwnerKey;
        if (!string.Equals(currentOwner, this.ownerKey, StringComparison.Ordinal))
        {
            this.ownerKey = currentOwner;
            this.friendCache = null;
            this.presenceCache.Clear();
            this.ResetFriendRefresh();
            this.CompleteFriendWaiters(PlayerListStatus.IdentityChanged, "Character changed.");
            this.CompletePresenceWaiters(PlayerListStatus.IdentityChanged, PresenceState.Unknown);
            this.DrainQueues(PlayerListStatus.IdentityChanged);
            lock (this.nameGate)
            {
                Array.Clear(this.localLinkshells);
                Array.Clear(this.crossLinkshells);
            }
        }

        this.RefreshLinkshellNames();
        this.TickFriendList();
        this.TickPresence();
    }

    /// <summary>Queue clearing on the next framework tick.</summary>
    public void Clear() => Interlocked.Exchange(ref this.clearRequested, 1);

    private HashSet<ulong>? FriendIds => this.friendCache?.ContentIds;

    private string? CurrentOwnerKey => this.clientState.IsLoggedIn && this.playerState.IsLoaded
        ? $"{this.playerState.CharacterName}@{this.playerState.HomeWorld.RowId}"
        : null;

    private bool OwnerMatches(SocialRequestContext context) =>
        string.IsNullOrEmpty(context.OwnerKey)
        || string.Equals(context.OwnerKey, this.CurrentOwnerKey, StringComparison.Ordinal);

    private void ClearState(PlayerListStatus status)
    {
        Interlocked.Exchange(ref this.clearRequested, 0);
        this.ownerKey = null;
        this.nextLinkshellRefresh = 0;
        lock (this.nameGate)
        {
            Array.Clear(this.localLinkshells);
            Array.Clear(this.crossLinkshells);
        }

        this.friendCache = null;
        this.presenceCache.Clear();
        this.CompleteFriendWaiters(status, "No character is logged in.");
        this.CompletePresenceWaiters(status, PresenceState.Unknown);
        this.DrainQueues(status);
        this.ResetFriendRefresh();
        this.ResetPresence();
    }

    private void DrainQueues(PlayerListStatus status)
    {
        while (this.friendQueue.TryDequeue(out var friend))
        {
            Interlocked.Decrement(ref this.queuedFriendRequests);
            friend.Cancel();
            friend.Completion.TrySetResult(new FriendListData(status, Array.Empty<PlayerInfo>(), "Request cancelled."));
        }

        while (this.presenceQueue.TryDequeue(out var presence))
        {
            Interlocked.Decrement(ref this.queuedPresenceRequests);
            presence.Cancel();
            presence.Completion.TrySetResult(new FriendPresenceData(status, PresenceState.Unknown, "Request cancelled."));
        }
    }

    private unsafe void RefreshLinkshellNames()
    {
        if (Environment.TickCount64 < this.nextLinkshellRefresh)
            return;

        this.nextLinkshellRefresh = Environment.TickCount64 + (long)LinkshellRefreshInterval.TotalMilliseconds;
        try
        {
            var localNames = new string?[LinkshellSlots];
            var crossNames = new string?[LinkshellSlots];

            var cross = InfoProxyCrossWorldLinkshell.Instance();
            if (cross != null)
            {
                var names = cross->CrossWorldLinkshells;
                for (var i = 0; i < Math.Min(LinkshellSlots, names.Length); i++)
                    crossNames[i] = CleanName(names[i].Name.ToString());
            }

            var local = InfoProxyLinkshell.Instance();
            if (local != null)
            {
                for (uint i = 0; i < LinkshellSlots; i++)
                {
                    var info = local->GetLinkshellInfo(i);
                    localNames[i] = info == null || info->Id == 0
                        ? null
                        : CleanName(local->GetLinkshellName(info->Id).ToString());
                }
            }

            lock (this.nameGate)
            {
                Array.Copy(localNames, this.localLinkshells, LinkshellSlots);
                Array.Copy(crossNames, this.crossLinkshells, LinkshellSlots);
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Could not refresh linkshell names.");
            this.nextLinkshellRefresh = Environment.TickCount64 + 60_000;
        }
    }

    private void TickFriendList()
    {
        if (this.friendRefreshActive)
        {
            if (this.friendEndRequested)
            {
                this.friendEndRequested = false;
                this.CompleteFriendRefresh(this.ReadFriendSnapshot());
            }
            else if (this.friendEndRequestOffThread)
            {
                this.friendEndRequestOffThread = false;
                this.CompleteFriendRefresh(new FriendListData(
                    PlayerListStatus.Unavailable,
                    Array.Empty<PlayerInfo>(),
                    "Friend list callback was not on the framework thread."));
                this.ResetFriendRefresh();
            }
            else if (DateTimeOffset.UtcNow >= this.friendDeadline)
            {
                this.ResetFriendRefresh();
                this.CompleteFriendWaiters(PlayerListStatus.TimedOut, "Friend list request timed out.");
            }

            if (this.friendRefreshActive)
                return;
        }

        while (this.friendQueue.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref this.queuedFriendRequests);
            if (pending.Cancelled)
                continue;

            if (this.friendCache is { } cache
                && DateTimeOffset.UtcNow - cache.CapturedAt < FriendCacheLifetime)
            {
                pending.Completion.TrySetResult(new FriendListData(
                    PlayerListStatus.Success,
                    cache.Players));
                continue;
            }

            this.friendWaiters.Add(pending);
        }

        if (this.friendWaiters.Count == 0)
            return;

        if (this.friendCache is { } fresh
            && DateTimeOffset.UtcNow - fresh.CapturedAt < FriendCacheLifetime)
        {
            this.CompleteFriendWaiters(PlayerListStatus.Success, fresh.Players);
            return;
        }

        this.BeginFriendRefresh();
    }

    private unsafe void BeginFriendRefresh()
    {
        try
        {
            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null || proxy->VirtualTable == null)
            {
                this.CompleteFriendWaiters(PlayerListStatus.Unavailable, "Friend list is unavailable.");
                return;
            }

            if (proxy->EntryCount > MaxFriends || (proxy->EntryCount > 0 && proxy->CharData == null))
            {
                this.CompleteFriendWaiters(PlayerListStatus.Failed, "Friend list data is invalid.");
                return;
            }

            var address = ((nint*)proxy->VirtualTable)[10];
            if (address == 0)
            {
                this.CompleteFriendWaiters(PlayerListStatus.Unavailable, "Friend list callback is unavailable.");
                return;
            }

            if (this.endRequestHook is null || address != this.endRequestAddress)
            {
                this.endRequestHook?.Dispose();
                this.endRequestHook = this.interop.HookFromAddress<EndRequestDelegate>(address, this.OnEndRequest);
                this.endRequestAddress = address;
                this.endRequestHook.Enable();
            }

            this.watchedProxy = (nint)proxy;
            this.friendEndRequested = false;
            this.friendEndRequestOffThread = false;
            this.friendRefreshActive = true;
            this.friendDeadline = DateTimeOffset.UtcNow + RequestTimeout;

            if (!proxy->RequestData() && !this.friendEndRequested)
            {
                this.ResetFriendRefresh();
                this.CompleteFriendWaiters(PlayerListStatus.Busy, "Friend list is busy.");
            }
        }
        catch (Exception ex)
        {
            this.ResetFriendRefresh();
            this.log.Warning(ex, "Friend list request could not be started.");
            this.CompleteFriendWaiters(PlayerListStatus.Unavailable, "Friend list is unavailable.");
        }
    }

    private void OnEndRequest(nint proxy)
    {
        this.endRequestHook?.Original(proxy);
        if (this.disposed || proxy != this.watchedProxy)
            return;

        if (!this.framework.IsInFrameworkUpdateThread)
        {
            this.friendEndRequestOffThread = true;
            return;
        }

        this.friendEndRequested = true;
    }

    private unsafe FriendListData ReadFriendSnapshot()
    {
        try
        {
            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null || (nint)proxy != this.watchedProxy)
                return new FriendListData(PlayerListStatus.IdentityChanged, Array.Empty<PlayerInfo>(), "Friend list changed.");

            var count = proxy->EntryCount;
            if (count > MaxFriends || (count > 0 && proxy->CharData == null))
                return new FriendListData(PlayerListStatus.Failed, Array.Empty<PlayerInfo>(), "Friend list data is invalid.");

            var entries = proxy->CharDataSpan;
            if (entries.Length < count)
                return new FriendListData(PlayerListStatus.Failed, Array.Empty<PlayerInfo>(), "Friend list data is incomplete.");

            var players = new List<PlayerInfo>((int)count);
            var ids = new HashSet<ulong>();
            var worlds = this.data.GetExcelSheet<World>();
            var jobs = this.data.GetExcelSheet<ClassJob>();
            var territories = this.data.GetExcelSheet<TerritoryType>();

            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry.ContentId == 0 || !ids.Add(entry.ContentId))
                    return new FriendListData(PlayerListStatus.Failed, Array.Empty<PlayerInfo>(), "Friend list contains invalid identities.");

                var name = CleanName(entry.NameString) ?? string.Empty;
                var player = new PlayerInfo
                {
                    ContentId = entry.ContentId,
                    Name = name,
                    HomeWorldId = entry.HomeWorld,
                    HomeWorldName = worlds.GetRowOrDefault(entry.HomeWorld)?.Name.ExtractText() ?? string.Empty,
                    CurrentWorldId = entry.CurrentWorld,
                    CurrentWorldName = worlds.GetRowOrDefault(entry.CurrentWorld)?.Name.ExtractText() ?? string.Empty,
                    FreeCompanyTag = CleanName(entry.FCTagString) ?? string.Empty,
                    StatusBits = (ulong)entry.State,
                    JobId = entry.Job,
                    JobName = jobs.GetRowOrDefault(entry.Job)?.Name.ExtractText() ?? string.Empty,
                    TerritoryId = entry.Location,
                    TerritoryName = TerritoryName(territories, entry.Location),
                    GrandCompany = (uint)entry.GrandCompany,
                    Languages = (uint)entry.Languages,
                    MainLanguage = (uint)entry.ClientLanguage,
                    IdentityUnavailable = entry.HomeWorld == 0 || name.Length == 0,
                };
                players.Add(player);
            }

            var snapshot = new FriendCache(
                players,
                ids,
                DateTimeOffset.UtcNow);
            this.friendCache = snapshot;
            return new FriendListData(PlayerListStatus.Success, players);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Friend list was received but could not be read.");
            return new FriendListData(PlayerListStatus.Failed, Array.Empty<PlayerInfo>(), "Friend list could not be read.");
        }
    }

    private void CompleteFriendRefresh(FriendListData data)
    {
        this.friendRefreshActive = false;
        if (data.Status == PlayerListStatus.Success)
            this.CompleteFriendWaiters(PlayerListStatus.Success, data.Players);
        else
            this.CompleteFriendWaiters(data.Status, data.Message);
    }

    private void CompleteFriendWaiters(PlayerListStatus status, string message)
    {
        foreach (var waiter in this.friendWaiters)
            waiter.Completion.TrySetResult(new FriendListData(status, Array.Empty<PlayerInfo>(), message));
        this.friendWaiters.Clear();
    }

    private void CompleteFriendWaiters(PlayerListStatus status, IReadOnlyList<PlayerInfo> players)
    {
        foreach (var waiter in this.friendWaiters)
            waiter.Completion.TrySetResult(new FriendListData(status, players));
        this.friendWaiters.Clear();
    }

    private void ResetFriendRefresh()
    {
        this.friendRefreshActive = false;
        this.friendEndRequested = false;
        this.friendEndRequestOffThread = false;
    }

    private void TickPresence()
    {
        if (this.presenceActive)
        {
            if (this.presenceCompleted)
            {
                var state = this.presenceCode switch
                {
                    0 => PresenceState.Online,
                    1 => PresenceState.Offline,
                    2 => PresenceState.Unknown,
                    _ => PresenceState.Unknown,
                };
                var status = this.presenceCode <= 2
                    ? PlayerListStatus.Success
                    : PlayerListStatus.Unavailable;
                if (status == PlayerListStatus.Success)
                {
                    if (this.presenceCache.Count >= MaxFriends)
                        this.presenceCache.Clear();
                    this.presenceCache[this.presenceContentId] = (state, DateTimeOffset.UtcNow);
                }

                this.activePresence?.Completion.TrySetResult(new FriendPresenceData(status, state));
                this.ResetPresence();
            }
            else if (this.presenceResponseOffThread)
            {
                this.activePresence?.Completion.TrySetResult(new FriendPresenceData(
                    PlayerListStatus.Unavailable,
                    PresenceState.Unknown,
                    "Presence callback was not on the framework thread."));
                this.ResetPresence();
            }
            else if (DateTimeOffset.UtcNow >= this.presenceDeadline)
            {
                this.activePresence?.Completion.TrySetResult(new FriendPresenceData(
                    PlayerListStatus.TimedOut,
                    PresenceState.Unknown,
                    "Presence request timed out."));
                this.ResetPresence();
            }

            if (this.presenceActive)
                return;
        }

        while (this.presenceQueue.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref this.queuedPresenceRequests);
            if (pending.Cancelled)
                continue;

            if (this.FriendIds is not { } ids || !ids.Contains(pending.ContentId))
            {
                pending.Completion.TrySetResult(new FriendPresenceData(
                    PlayerListStatus.Unavailable,
                    PresenceState.Unknown,
                    "ContentId is not in the current friend snapshot."));
                continue;
            }

            if (this.presenceCache.TryGetValue(pending.ContentId, out var cached)
                && DateTimeOffset.UtcNow - cached.At < PresenceCacheLifetime)
            {
                pending.Completion.TrySetResult(new FriendPresenceData(PlayerListStatus.Success, cached.State));
                continue;
            }

            this.BeginPresence(pending);
            return;
        }
    }

    private unsafe void BeginPresence(PendingPresenceRequest pending)
    {
        try
        {
            var proxy = InfoProxyFriendList.Instance();
            var agent = AgentFriendlist.Instance();
            if (proxy == null || proxy->VirtualTable == null || agent == null)
            {
                pending.Completion.TrySetResult(new FriendPresenceData(
                    PlayerListStatus.Unavailable,
                    PresenceState.Unknown,
                    "Presence query is unavailable."));
                return;
            }

            var address = ((nint*)proxy->VirtualTable)[9];
            if (address == 0)
            {
                pending.Completion.TrySetResult(new FriendPresenceData(
                    PlayerListStatus.Unavailable,
                    PresenceState.Unknown,
                    "Presence callback is unavailable."));
                return;
            }

            if (this.presenceHook is null || address != this.presenceAddress)
            {
                this.presenceHook?.Dispose();
                this.presenceHook = this.interop.HookFromAddress<PresenceResponseDelegate>(address, this.OnPresenceResponse);
                this.presenceAddress = address;
                this.presenceHook.Enable();
            }

            this.watchedProxy = (nint)proxy;
            this.activePresence = pending;
            this.presenceContentId = pending.ContentId;
            this.presenceCompleted = false;
            this.presenceResponseOffThread = false;
            this.presenceActive = true;
            this.presenceDeadline = DateTimeOffset.UtcNow + RequestTimeout;
            agent->RequestFriendInfo(pending.ContentId);
        }
        catch (Exception ex)
        {
            this.ResetPresence();
            this.log.Warning(ex, "Presence query could not be started.");
            pending.Completion.TrySetResult(new FriendPresenceData(
                PlayerListStatus.Unavailable,
                PresenceState.Unknown,
                "Presence query is unavailable."));
        }
    }

    private unsafe void OnPresenceResponse(nint proxy, nint response, ulong contentId)
    {
        this.presenceHook?.Original(proxy, response, contentId);
        if (this.disposed || proxy != this.watchedProxy || contentId == 0 || contentId != this.presenceContentId)
            return;

        if (!this.framework.IsInFrameworkUpdateThread)
        {
            this.presenceResponseOffThread = true;
            return;
        }

        try
        {
            this.presenceCode = response != 0 && *(ulong*)((byte*)response + 8) == contentId
                ? *(ushort*)((byte*)response + 0x14)
                : (ushort)0xFFFF;
            this.presenceCompleted = true;
        }
        catch
        {
            this.presenceCompleted = false;
            this.presenceResponseOffThread = true;
        }
    }

    private void CompletePresenceWaiters(PlayerListStatus status, PresenceState state)
    {
        this.activePresence?.Completion.TrySetResult(new FriendPresenceData(status, state, "Request cancelled."));
        this.ResetPresence();

        while (this.presenceQueue.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref this.queuedPresenceRequests);
            pending.Cancel();
            pending.Completion.TrySetResult(new FriendPresenceData(status, state, "Request cancelled."));
        }
    }

    private void ResetPresence()
    {
        this.presenceActive = false;
        this.presenceCompleted = false;
        this.presenceResponseOffThread = false;
        this.presenceContentId = 0;
        this.activePresence = null;
    }

    private static string? CleanName(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length > 64 ? value[..64] : value;
    }

    private static string TerritoryName(
        Lumina.Excel.ExcelSheet<TerritoryType> territories,
        ushort id)
    {
        var row = territories.GetRowOrDefault(id);
        if (row is not { } territory || !territory.PlaceName.IsValid)
            return string.Empty;
        return territory.PlaceName.Value.Name.ExtractText();
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.endRequestHook?.Dispose();
        this.presenceHook?.Dispose();
        this.DrainQueues(PlayerListStatus.Unavailable);
    }

    private sealed class PendingFriendRequest
    {
        public PendingFriendRequest(SocialRequestContext context) => this.Context = context;

        public SocialRequestContext Context { get; }
        public TaskCompletionSource<FriendListData> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled => Volatile.Read(ref this.cancelled) != 0;
        private int cancelled;

        public void Cancel() => Interlocked.Exchange(ref this.cancelled, 1);
    }

    private sealed class PendingPresenceRequest
    {
        public PendingPresenceRequest(ulong contentId, SocialRequestContext context)
        {
            this.ContentId = contentId;
            this.Context = context;
        }

        public ulong ContentId { get; }
        public SocialRequestContext Context { get; }
        public TaskCompletionSource<FriendPresenceData> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled => Volatile.Read(ref this.cancelled) != 0;
        private int cancelled;

        public void Cancel() => Interlocked.Exchange(ref this.cancelled, 1);
    }

    private sealed record FriendCache(
        IReadOnlyList<PlayerInfo> Players,
        HashSet<ulong> ContentIds,
        DateTimeOffset CapturedAt);
}
