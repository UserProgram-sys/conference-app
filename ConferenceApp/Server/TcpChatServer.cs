using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Conference.Shared;
namespace Conference.Server;

/// <summary>
/// TCP control + chat server. Line protocol (client -> server):
/// NAME n | ROOMS | CREATE r [pass] | DELETE r | JOIN r [pass] | LEAVE | MSG text | QUIT
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
        Logger.Info($"[TCP] Chat server listening on {port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                if (_sessions.Count >= Limits.MaxClients) { _ = RejectAsync(tcp); continue; }
                tcp.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(tcp));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private static async Task RejectAsync(TcpClient tcp)
    {
        try { await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes("ERR Server full\n")); } catch { }
        tcp.Close();
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        var s = new ClientSession(Interlocked.Increment(ref _nextId), tcp);
        _sessions[s.Id] = s;
        Logger.Info($"Client #{s.Id} connected from {tcp.Client.RemoteEndPoint}");
        try
        {
            using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
            string? line;
            while ((line = await ReadLineAsync(reader)) != null)
            {
                if (line.Length == 0) continue;
                var first = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                var cmd = first[0].ToUpperInvariant();
                var arg = first.Length > 1 ? first[1] : "";

                if (cmd == "QUIT") break;
                if (cmd == "NAME") { await HandleNameAsync(s, arg); continue; }
                if (!s.Named) { await s.SendAsync("ERR Send NAME first"); continue; }
                await DispatchAsync(s, cmd, arg);
            }
        }
        catch (InvalidDataException)
        {
            Logger.Info($"Client #{s.Id} sent an oversized line - disconnecting");
            await s.SendAsync("ERR Line too long");
        }
        catch (IOException) { }
        finally
        {
            await _rooms.LeaveAsync(s);
            _sessions.TryRemove(s.Id, out _);
            tcp.Close();
            Logger.Info($"Client #{s.Id} ({s.Name}) disconnected");
        }
    }

    private async Task HandleNameAsync(ClientSession s, string arg)
    {
        if (s.Named) { await s.SendAsync("ERR Name already set"); return; }
        var wanted = Sanitize(arg, "user");
        if (wanted.Length > Limits.MaxNameLength - 3) wanted = wanted[..(Limits.MaxNameLength - 3)];
        var name = wanted;
        for (int n = 2; _sessions.Values.Any(x => x != s && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = wanted + n;                                   // names are unique on the server
        s.Name = name;
        await s.SendAsync($"WELCOME {s.Id} {s.Token} {s.Name}");
    }

    private async Task DispatchAsync(ClientSession s, string cmd, string arg)
    {
        switch (cmd)
        {
            case "ROOMS": await s.SendAsync("ROOMS " + _rooms.List()); break;
            case "CREATE":
            {
                var (name, pass) = SplitRoomArg(arg);
                var r = _rooms.Create(Sanitize(name, ""), pass, s);
                await Reply(s, r);
                if (r.Ok) Logger.Info($"{s.Name} created room '{name}'");
                break;
            }
            case "DELETE":
            {
                var r = await _rooms.DeleteAsync(arg, s);
                await Reply(s, r);
                if (r.Ok) Logger.Info($"{s.Name} deleted room '{arg}'");
                break;
            }
            case "JOIN":
            {
                var (name, pass) = SplitRoomArg(arg);
                var r = await _rooms.JoinAsync(name, pass, s);
                if (!r.Ok) await Reply(s, r); else Logger.Info($"{s.Name} joined '{name}'");
                break;
            }
            case "LEAVE":
                await _rooms.LeaveAsync(s);
                await s.SendAsync("ROOM_LEFT");
                break;
            case "MSG":
                if (s.Room == null) await s.SendAsync("ERR Join a room first");
                else if (!s.ChatLimiter.TryTake()) await s.SendAsync("ERR Slow down");
                else await s.Room.BroadcastAsync($"CHAT {s.Name} {arg}");
                break;
            default: await s.SendAsync("ERR Unknown command"); break;
        }
    }

    /// <summary>Reads one line but refuses lines longer than Limits.MaxLineLength.</summary>
    private static async Task<string?> ReadLineAsync(StreamReader r)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (true)
        {
            if (await r.ReadAsync(buf.AsMemory(0, 1)) == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= Limits.MaxLineLength) throw new InvalidDataException("line too long");
            sb.Append(buf[0]);
        }
    }

    private static (string Name, string? Pass) SplitRoomArg(string arg)
    {
        var p = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
        return (p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : null);
    }

    private static Task Reply(ClientSession s, (bool Ok, string Msg) r) => s.SendAsync((r.Ok ? "OK " : "ERR ") + r.Msg);

    private static string Sanitize(string v, string fallback)
    {
        var t = new string(v.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        if (t.Length > Limits.MaxNameLength) t = t[..Limits.MaxNameLength];
        return t.Length == 0 ? fallback : t;
    }
}
