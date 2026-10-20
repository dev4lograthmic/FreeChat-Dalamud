using System.Text;
using FreeChat.Protocol.V1;
using Google.Protobuf;

namespace FreeChat.Protocol;

/// <summary>
/// Encodes one <see cref="Envelope"/> for a WebSocket message. The caller
/// chooses the negotiated encoding; the payload is identical.
/// </summary>
public static class EnvelopeCodec
{
    public static byte[] Encode(Envelope envelope, bool json) =>
        json
            ? Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(envelope))
            : envelope.ToByteArray();

    public static Envelope Decode(ReadOnlySpan<byte> data, bool json) =>
        json
            ? JsonParser.Default.Parse<Envelope>(Encoding.UTF8.GetString(data))
            : Envelope.Parser.ParseFrom(data.ToArray());
}
