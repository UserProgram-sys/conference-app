using System.Net.Sockets;
using System.Text;
namespace Conference.Client;

/// <summary>TCP side of the client: pure networking, no UI code.</summary>
public sealed class ChatClient : IDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim _sendLock = new(1, 1);   // writes may come from several threads
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public event Action<string>? LineReceived;
    public event Action? Disconnected;

    public async Task ConnectAsync(string host, int port)
    {
        await _tcp.ConnectAsync(host, port);
        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void StartReading() => Task.Run(async () =>
    {
        try { string? l; while ((l = await _reader!.ReadLineAsync()) != null) LineReceived?.Invoke(l); }
        catch { }
        Disconnected?.Invoke();
    });

    public async Task SendAsync(string line)
    {
        await _sendLock.WaitAsync();
        try { await _writer!.WriteLineAsync(line); }
        finally { _sendLock.Release(); }
    }

    public void Dispose() => _tcp.Close();
}
