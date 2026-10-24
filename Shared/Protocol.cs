using System.Buffers.Binary;
namespace Conference.Shared;

/// <summary>Ports used by the conference system.</summary>
public static class Protocol
{
    public const int TcpPort = 5000;   // chat + control (line based text)
    public const int UdpPort = 5001;   // audio / video relay
}

public enum PacketType : byte { Hello = 0, Audio = 1, Video = 2 }

/// <summary>
/// UDP packet header (13 bytes, big endian):
/// [Type:1][SessionId:4][Seq:4][FragIndex:2][FragCount:2] followed by payload.
/// Audio: Seq = packet counter, FragCount = 1.
/// Video: Seq = frame id, a JPEG frame is split into FragCount fragments.
/// </summary>
public readonly record struct MediaHeader(PacketType Type, int SessionId, int Seq, ushort FragIndex, ushort FragCount);

public static class MediaPacket
{
    public const int HeaderSize = 13;
    public const int MaxPayload = 1200; // stays under typical MTU

    public static byte[] Build(PacketType type, int sessionId, int seq, ushort index, ushort count, ReadOnlySpan<byte> payload)
    {
        var b = new byte[HeaderSize + payload.Length];
        b[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), sessionId);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(5), seq);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(9), index);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(11), count);
        payload.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    public static bool TryParse(byte[] d, out MediaHeader h)
    {
        h = default;
        if (d.Length < HeaderSize || d[0] > (byte)PacketType.Video) return false;
        h = new MediaHeader((PacketType)d[0],
            BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(1)),
            BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(5)),
            BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(9)),
            BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(11)));
        return true;
    }
}
