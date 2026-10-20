using System.Security.Cryptography;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using FreeChat.Server;
using Google.Protobuf;
using Xunit;

namespace FreeChat.Server.Tests;

public sealed class CoreTests
{
    [Fact]
    public void EnvelopeCodec_RoundTrips_BinaryAndJson()
    {
        var envelope = new Envelope
        {
            RequestId = "r1",
            ChatEvent = TestKeys.Chat("Hello"),
        };

        var binary = EnvelopeCodec.Decode(EnvelopeCodec.Encode(envelope, json: false), json: false);
        var json = EnvelopeCodec.Decode(EnvelopeCodec.Encode(envelope, json: true), json: true);

        Assert.Equal("Hello", binary.ChatEvent.Text);
        Assert.Equal("Hello", json.ChatEvent.Text);
        Assert.Equal(ChatChannel.Say, json.ChatEvent.Channel);
    }

    [Fact]
    public void ChatFilter_AppliesCategoriesKeywordRegexAndOwnFlag()
    {
        var chatEvent = TestKeys.Chat("Hello world", ChatSource.PostHandle);

        Assert.True(new ChatFilterMatcher(new ChatFilter
        {
            IncludeCategories = { ChatCategory.PlayerChat },
        }).Matches(chatEvent));

        Assert.False(new ChatFilterMatcher(new ChatFilter
        {
            ExcludeCategories = { ChatCategory.PlayerChat },
        }).Matches(chatEvent));

        Assert.True(new ChatFilterMatcher(new ChatFilter
        {
            Keyword = "world",
        }).Matches(chatEvent));

        Assert.False(new ChatFilterMatcher(new ChatFilter
        {
            Keyword = "missing",
        }).Matches(chatEvent));

        Assert.True(new ChatFilterMatcher(new ChatFilter
        {
            Regex = "^Hello",
        }).Matches(chatEvent));

        chatEvent.Outgoing = true;
        Assert.True(new ChatFilterMatcher(new ChatFilter()).Matches(chatEvent));
        Assert.False(new ChatFilterMatcher(new ChatFilter
        {
            IncludeOwn = false,
        }).Matches(chatEvent));
    }

    [Fact]
    public void ChatFilter_RejectsInvalidRegex()
    {
        var ex = Assert.Throws<FilterValidationException>(() =>
            new ChatFilterMatcher(new ChatFilter { Regex = "[" }));
        Assert.Equal(ErrorCode.RegexInvalid, ex.Code);
    }

    [Fact]
    public void ChatBacklog_TrimsAndReplaysBySequence()
    {
        var backlog = new ChatBacklog(maxMessages: 2, maxBytes: 1024 * 1024);
        for (var i = 1; i <= 3; i++)
        {
            var chat = TestKeys.Chat($"m{i}");
            chat.Sequence = i;
            backlog.Append(chat);
        }

        Assert.Equal(2, backlog.Count);
        Assert.Equal(2, backlog.OldestSequence);
        Assert.Equal(3, backlog.LatestSequence);
        Assert.Equal(new long[] { 2, 3 }, backlog.After(1).Select(x => x.Event.Sequence).ToArray());
    }

    [Fact]
    public async Task Pairing_ThenChallenge_VerifiesSignature()
    {
        var clock = new FakeClock();
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, new AutoApprovePairing(), clock);
        var (key, publicKey) = TestKeys.Create();

        var result = await pairing.PairAsync(
            pairing.CurrentCode,
            new PairingRequest(null, "test device", "test", publicKey),
            "127.0.0.1",
            CancellationToken.None);

        Assert.True(result.Ok);
        var device = Assert.Single(store.List());
        var challenge = DeviceAuthenticator.CreateChallenge(clock);
        var input = DeviceAuthenticator.BuildChallengeInput("server", device.DeviceId, challenge.Nonce);
        var signature = key.SignData(
            input,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(DeviceAuthenticator.Verify(
            device, "server", device.DeviceId, challenge.Nonce, signature));
        Assert.False(DeviceAuthenticator.Verify(
            device, "server", device.DeviceId, challenge.Nonce, new byte[64]));

        key.Dispose();
    }

    [Fact]
    public async Task Pairing_WrongCodeAndExpiry_AreRejected()
    {
        var clock = new FakeClock();
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, new AutoApprovePairing(), clock);
        var (key, publicKey) = TestKeys.Create();

        var wrongCode = pairing.CurrentCode == "000000" ? "000001" : "000000";
        var wrong = await pairing.PairAsync(
            wrongCode,
            new PairingRequest(null, "test", "test", publicKey),
            "127.0.0.1",
            CancellationToken.None);
        Assert.False(wrong.Ok);
        Assert.Equal(ErrorCode.PairingInvalid, wrong.Error);

        clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await pairing.PairAsync(
            pairing.CurrentCode,
            new PairingRequest(null, "test", "test", publicKey),
            "127.0.0.1",
            CancellationToken.None);
        Assert.False(expired.Ok);
        Assert.Equal(ErrorCode.PairingExpired, expired.Error);
        key.Dispose();
    }

    [Fact]
    public void TokenBucket_RefillsOverTime()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(capacity: 2, refillPerSecond: 1, clock);

        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(bucket.TryTake());
    }
}
