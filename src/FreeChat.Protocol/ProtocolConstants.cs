namespace FreeChat.Protocol;

public static class ProtocolConstants
{
    public const uint Version = 1;
    public const string ProtobufSubProtocol = "freechat.v1.protobuf";
    public const string JsonSubProtocol = "freechat.v1.json";
    public const string FriendListFeature = "social.friend_list";
    public const string FriendPresenceFeature = "social.friend_presence";

    public static readonly string[] DefaultFeatures =
    [
        "chat.segments",
        "chat.chunks",
        "chat.chunks.party_finder",
        "chat.chunks.quest",
        "chat.raw",
        "chat.original",
        "chat.sources.pre_handle",
        "chat.sources.post_handle",
        "chat.sources.structured",
        "filters.regex",
        "replay.sequence",
        "send.raw",
        "send.structured",
        "send.cancel",
        "send.echo",
        "pairing.qr",
        "pairing.device_keys",
        "tls.pinning.sha256",
    ];
}
