using System.Text;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using Xunit;

namespace FreeChat.Server.Tests;

public sealed class ConformanceTests
{
    [Fact]
    public void EnvelopeCodec_JsonAndProtobufCarryTheSameFields()
    {
        var envelope = new Envelope
        {
            RequestId = "req-1",
            ChatEvent = new ChatEvent
            {
                MessageId = "srv:sess:42",
                Sequence = 42,
                Channel = ChatChannel.Say,
                LogKind = 10,
                Source = ChatSource.PostHandle,
                Text = "Hello",
                Sender = new CharacterRef
                {
                    Name = "Someone",
                    WorldId = 86,
                    WorldName = "Typhon",
                    IsPlayer = true,
                },
            },
        };
        envelope.ChatEvent.Segments.Add(new Segment
        {
            Style = new TextStyle { ForegroundRgba = 0xFFFFFFFF },
            Text = new TextSegment { Text = "Hello" },
        });

        var protobuf = EnvelopeCodec.Encode(envelope, json: false);
        var json = EnvelopeCodec.Encode(envelope, json: true);

        var decodedProtobuf = EnvelopeCodec.Decode(protobuf, json: false);
        var decodedJson = EnvelopeCodec.Decode(json, json: true);

        Assert.Equal("req-1", decodedProtobuf.RequestId);
        Assert.Equal("srv:sess:42", decodedProtobuf.ChatEvent.MessageId);
        Assert.Equal(42, decodedProtobuf.ChatEvent.Sequence);
        Assert.Equal(ChatChannel.Say, decodedProtobuf.ChatEvent.Channel);
        Assert.Equal("Hello", decodedProtobuf.ChatEvent.Text);
        Assert.Single(decodedProtobuf.ChatEvent.Segments);

        Assert.Equal(decodedProtobuf.ChatEvent.Text, decodedJson.ChatEvent.Text);
        Assert.Equal(decodedProtobuf.ChatEvent.Sender.Name, decodedJson.ChatEvent.Sender.Name);
        Assert.Equal(decodedProtobuf.ChatEvent.Segments.Count, decodedJson.ChatEvent.Segments.Count);
        Assert.Contains("\"chatEvent\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.Contains("\"requestId\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolConstants_MatchTheV1Contract()
    {
        Assert.Equal(1u, ProtocolConstants.Version);
        Assert.Equal("freechat.v1.protobuf", ProtocolConstants.ProtobufSubProtocol);
        Assert.Equal("freechat.v1.json", ProtocolConstants.JsonSubProtocol);
        Assert.Contains("tls.pinning.sha256", ProtocolConstants.DefaultFeatures);
        Assert.Contains("send.structured", ProtocolConstants.DefaultFeatures);
    }

    [Fact]
    public void ChatFilter_IncludeOwnIsOptional()
    {
        var filter = new ChatFilter();
        Assert.False(filter.HasIncludeOwn);

        filter.IncludeOwn = false;
        Assert.True(filter.HasIncludeOwn);
        Assert.False(filter.IncludeOwn);
    }
}
