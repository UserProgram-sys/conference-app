using System.Buffers.Binary;
using System.Diagnostics;
namespace Conference.Shared;

/// <summary>Ports used by the conference system.</summary>
public static class Protocol
{
    public const int TcpPort = 5000;   // chat + control (line based text)
    public const int UdpPort = 5001;   // audio / video relay
}

/// <summary>Server-side safety limits (stability / abuse protection).</summary>
public static class Limits
{
    public const int MaxLineLength = 1000;      // longest accepted TCP line
    public const int MaxClients = 200;
    public const int MaxRooms = 100;
    public const int MaxRoomMembers = 20;
    public const int MaxNameLength = 20;
    public const int MaxPasswordLength = 50;
    public const double ChatPerSecond = 5, ChatBurst = 10;
    public const double UdpPerSecond = 1500, UdpBurst = 3000;
    public const int MaxUdpPacket = 1500;
}

/// <summary>Thread-safe token-bucket rate limiter.</summary>
public sealed class RateLimiter
{
    private readonly double _rate, _burst;
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();
    private readonly object _lock = new();

    public RateLimiter(double perSecond, double burst) { _rate = perSecond; _burst = burst; _tokens = burst; }

    public bool TryTake()
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(_burst, _tokens + (now - _last) / (double)Stopwatch.Frequency * _rate);
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}

public enum PacketType : byte { Hello = 0, Audio = 1, Video = 2 }

/// <summary>
/// UDP packet header (13 bytes, big endian):
/// [Type:1][SessionId:4][Seq:4][FragIndex:2][FragCount:2] followed by payload.
/// Hello: payload = the secret token the server issued in WELCOME (proves ownership of the session).
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
