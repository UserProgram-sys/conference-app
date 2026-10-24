using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Conference.Server;

/// <summary>One connected client (TCP control connection + its UDP endpoint).</summary>
public sealed class ClientSession
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1); // serialize writes from many threads

    public ClientSession(int id, TcpClient tcp)
    {
        Id = id; Tcp = tcp;
        _writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public int Id { get; }
    public TcpClient Tcp { get; }
    public string Name { get; set; } = "";
    public bool Named => Name.Length > 0;
    public volatile Room? Room;
    public volatile IPEndPoint? UdpEndPoint;   // registered by the UDP "Hello" packet

    public async Task SendAsync(string line)
    {
        await _sendLock.WaitAsync();
        try { await _writer.WriteLineAsync(line); }
        catch { /* client gone; read loop will clean up */ }
        finally { _sendLock.Release(); }
    }
}

public sealed class Room
{
    private readonly List<ClientSession> _members = new();
    private readonly object _lock = new();

    public Room(string name, ClientSession owner) { Name = name; Owner = owner; }
    public string Name { get; }
    public ClientSession Owner { get; }

    public void Add(ClientSession s) { lock (_lock) _members.Add(s); }
    public void Remove(ClientSession s) { lock (_lock) _members.Remove(s); }
    public ClientSession[] Snapshot() { lock (_lock) return _members.ToArray(); }

    public async Task BroadcastAsync(string line, ClientSession? except = null)
    {
        foreach (var m in Snapshot())
            if (m != except) await m.SendAsync(line);
    }
}
