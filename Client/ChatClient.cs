using System.Net.Sockets;
using System.Text;
namespace Conference.Client;

/// <summary>TCP side of the client: pure networking, no console code.</summary>
public sealed class ChatClient : IDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
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

    public Task SendAsync(string line) => _writer!.WriteLineAsync(line);
    public void Dispose() => _tcp.Close();
}
