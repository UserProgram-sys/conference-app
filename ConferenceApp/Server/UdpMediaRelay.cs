using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Conference.Shared;
namespace Conference.Server;

/// <summary>
/// UDP audio/video relay running on its own dedicated thread (separate from chat).
/// A "Hello" packet carrying the session token registers the client's UDP endpoint;
/// every other packet is forwarded to the other members of the sender's room.
/// </summary>
public sealed class UdpMediaRelay
{
    private readonly ConcurrentDictionary<int, ClientSession> _sessions;
    public UdpMediaRelay(ConcurrentDictionary<int, ClientSession> sessions) => _sessions = sessions;

    public void Run(int port, CancellationToken ct)
    {
        using var udp = new UdpClient(port);
        udp.Client.ReceiveBufferSize = 1 << 20;
        Logger.Info($"[UDP] Media relay listening on {port}");
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            byte[] data;
            try { data = udp.Receive(ref remote); }
            catch (SocketException) { continue; }          // e.g. ICMP "port unreachable" on Windows

            if (data.Length > Limits.MaxUdpPacket) continue;
            if (!MediaPacket.TryParse(data, out var h)) continue;
            if (!_sessions.TryGetValue(h.SessionId, out var sender)) continue;

            if (h.Type == PacketType.Hello)
            {
                // Only the owner of the TCP session knows the token -> nobody can hijack another client's stream.
                var got = new ReadOnlySpan<byte>(data, MediaPacket.HeaderSize, data.Length - MediaPacket.HeaderSize);
                if (CryptographicOperations.FixedTimeEquals(got, Encoding.ASCII.GetBytes(sender.Token)))
                    sender.UdpEndPoint = new IPEndPoint(remote.Address, remote.Port);
                continue;
            }

            // ignore packets that don't come from the registered endpoint (spoofing check)
            if (sender.UdpEndPoint is null || !sender.UdpEndPoint.Equals(remote)) continue;
            if (!sender.UdpLimiter.TryTake()) continue;    // flood protection
            var room = sender.Room;
            if (room == null) continue;

            foreach (var m in room.Snapshot())
                if (m != sender && m.UdpEndPoint is { } dest)
                    udp.Send(data, data.Length, dest);
        }
    }
}
