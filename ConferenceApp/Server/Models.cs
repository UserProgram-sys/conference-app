using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Conference.Shared;
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
    /// <summary>Secret the client must echo in its UDP Hello packet (prevents endpoint hijacking).</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    public string Name { get; set; } = "";
    public bool Named => Name.Length > 0;
    public volatile Room? Room;
    public volatile IPEndPoint? UdpEndPoint;
    public RateLimiter ChatLimiter { get; } = new(Limits.ChatPerSecond, Limits.ChatBurst);
    public RateLimiter UdpLimiter { get; } = new(Limits.UdpPerSecond, Limits.UdpBurst);

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
    private readonly byte[]? _passwordHash;

    public Room(string name, ClientSession owner, string? password)
    {
        Name = name; Owner = owner;
        if (!string.IsNullOrEmpty(password)) _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    public string Name { get; }
    public ClientSession Owner { get; }
    public bool Locked => _passwordHash != null;
    public int Count { get { lock (_lock) return _members.Count; } }

    public bool CheckPassword(string? password)
    {
        if (_passwordHash == null) return true;
        if (password == null) return false;
        return CryptographicOperations.FixedTimeEquals(_passwordHash, SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }

    public void Add(ClientSession s) { lock (_lock) _members.Add(s); }
    public void Remove(ClientSession s) { lock (_lock) _members.Remove(s); }
    public ClientSession[] Snapshot() { lock (_lock) return _members.ToArray(); }

    public async Task BroadcastAsync(string line, ClientSession? except = null)
    {
        foreach (var m in Snapshot())
            if (m != except) await m.SendAsync(line);
    }
}
