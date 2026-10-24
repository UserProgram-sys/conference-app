using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Conference.Server;

/// <summary>
/// TCP control + chat server. Line protocol (client -> server):
/// NAME n | ROOMS | CREATE r | DELETE r | JOIN r | LEAVE | MSG text | QUIT
/// One async task per client keeps the server responsive with many participants.
/// </summary>
public sealed class TcpChatServer
{
    private readonly RoomManager _rooms;
    private readonly ConcurrentDictionary<int, ClientSession> _sessions;
    private int _nextId;

    public TcpChatServer(RoomManager rooms, ConcurrentDictionary<int, ClientSession> sessions)
    { _rooms = rooms; _sessions = sessions; }

    public async Task RunAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[TCP] Chat server listening on {port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                tcp.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(tcp));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        var s = new ClientSession(Interlocked.Increment(ref _nextId), tcp);
        _sessions[s.Id] = s;
        Console.WriteLine($"[TCP] Client #{s.Id} connected from {tcp.Client.RemoteEndPoint}");
        try
        {
            using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                var cmd = parts[0].ToUpperInvariant();
                var arg = parts.Length > 1 ? parts[1] : "";

                if (cmd == "QUIT") break;
                if (cmd == "NAME")
                {
                    s.Name = Sanitize(arg, "user");
                    await s.SendAsync($"WELCOME {s.Id}");
                    continue;
                }
                if (!s.Named) { await s.SendAsync("ERR Send NAME first"); continue; }

                switch (cmd)
                {
                    case "ROOMS": await s.SendAsync("ROOMS " + _rooms.List()); break;
                    case "CREATE": await Reply(s, _rooms.Create(Sanitize(arg, ""), s)); break;
                    case "DELETE": await Reply(s, await _rooms.DeleteAsync(arg, s)); break;
                    case "JOIN":   var j = await _rooms.JoinAsync(arg, s); if (!j.Ok) await Reply(s, j); break;
                    case "LEAVE":  await _rooms.LeaveAsync(s); await s.SendAsync("ROOM_LEFT"); break;
                    case "MSG":
                        if (s.Room == null) await s.SendAsync("ERR Join a room first");
                        else await s.Room.BroadcastAsync($"CHAT {s.Name} {arg}");
                        break;
                    default: await s.SendAsync("ERR Unknown command"); break;
                }
            }
        }
        catch (IOException) { }
        finally
        {
            await _rooms.LeaveAsync(s);
            _sessions.TryRemove(s.Id, out _);
            tcp.Close();
            Console.WriteLine($"[TCP] Client #{s.Id} disconnected");
        }
    }

    private static Task Reply(ClientSession s, (bool Ok, string Msg) r) => s.SendAsync((r.Ok ? "OK " : "ERR ") + r.Msg);

    private static string Sanitize(string v, string fallback)
    {
        var t = new string(v.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.Length > 20) t = t[..20];
        return t.Length == 0 ? fallback : t;
    }
}
