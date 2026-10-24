using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Conference.Shared;
using NAudio.Wave;
using OpenCvSharp;
namespace Conference.Client;

/// <summary>
/// UDP side of the client. Threads: receive, keep-alive, video capture/send, display.
/// Audio: NAudio, 16 kHz / 16 bit / mono, 20 ms packets (640 bytes).
/// Video: OpenCvSharp webcam, 320x240 JPEG, ~15 fps, fragmented to fit the MTU.
/// </summary>
public sealed class MediaClient : IDisposable
{
    private static readonly WaveFormat Format = new(16000, 16, 1);

    private readonly UdpClient _udp = new();
    private readonly int _sessionId;
    private readonly Func<int, string> _nameOf;
    private readonly string _host; private readonly int _port;
    private volatile bool _running, _reset;
    private WaveInEvent? _mic;
    private int _audioSeq, _frameId;

    private readonly Dictionary<int, (BufferedWaveProvider Buf, WaveOutEvent Out)> _players = new();
    private readonly Dictionary<int, FrameAssembler> _assemblers = new();
    private readonly ConcurrentDictionary<int, Mat> _frames = new(); // key 0 = local preview

    public volatile bool Active;          // true only while inside a room
    public volatile bool AudioEnabled = true;
    public volatile bool VideoEnabled = true;

    public MediaClient(string host, int port, int sessionId, Func<int, string> nameOf)
    { _host = host; _port = port; _sessionId = sessionId; _nameOf = nameOf; }

    public void Start()
    {
        _udp.Connect(_host, _port);
        _running = true;
        SendHello();
        Spawn(ReceiveLoop, "UDP-Recv");
        Spawn(KeepAliveLoop, "UDP-KeepAlive");
        Spawn(VideoSendLoop, "Video-Send");
        Spawn(DisplayLoop, "Video-Display");
        StartMicrophone();
    }

    private static void Spawn(ThreadStart a, string n) => new Thread(a) { IsBackground = true, Name = n }.Start();

    private void SendHello() => _udp.Send(MediaPacket.Build(PacketType.Hello, _sessionId, 0, 0, 1, ReadOnlySpan<byte>.Empty));

    private void KeepAliveLoop()
    {
        while (_running) { try { SendHello(); } catch { } Thread.Sleep(5000); }
    }

    // ---------- Audio ----------
    private void StartMicrophone()
    {
        try
        {
            _mic = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 20 };
            _mic.DataAvailable += (_, e) =>
            {
                if (!Active || !AudioEnabled || e.BytesRecorded == 0) return;
                var pkt = MediaPacket.Build(PacketType.Audio, _sessionId, Interlocked.Increment(ref _audioSeq), 0, 1,
                    new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));
                try { _udp.Send(pkt); } catch { }
            };
            _mic.StartRecording();
        }
        catch (Exception ex) { Console.WriteLine($"[Audio] Microphone unavailable: {ex.Message}"); }
    }

    private void PlayAudio(int sender, byte[] pcm)
    {
        if (!_players.TryGetValue(sender, out var p))
        {
            var buf = new BufferedWaveProvider(Format)
            { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(500) };
            var wo = new WaveOutEvent();
            wo.Init(buf); wo.Play();
            _players[sender] = p = (buf, wo);
        }
        p.Buf.AddSamples(pcm, 0, pcm.Length);
    }

    // ---------- Video ----------
    private void VideoSendLoop()
    {
        using var cam = new VideoCapture(0);
        if (!cam.IsOpened()) { Console.WriteLine("[Video] No camera found - video sending disabled."); return; }
        while (_running)
        {
            if (!Active || !VideoEnabled) { Thread.Sleep(100); continue; }
            using var frame = new Mat();
            if (!cam.Read(frame) || frame.Empty()) { Thread.Sleep(50); continue; }

            var small = new Mat();
            Cv2.Resize(frame, small, new OpenCvSharp.Size(320, 240));
            _frames[0] = small;                                   // local preview

            Cv2.ImEncode(".jpg", small, out var jpg, new[] { (int)ImwriteFlags.JpegQuality, 60 });
            int id = Interlocked.Increment(ref _frameId);
            int count = (jpg.Length + MediaPacket.MaxPayload - 1) / MediaPacket.MaxPayload;
            for (int i = 0; i < count; i++)
            {
                int off = i * MediaPacket.MaxPayload;
                int len = Math.Min(MediaPacket.MaxPayload, jpg.Length - off);
                var pkt = MediaPacket.Build(PacketType.Video, _sessionId, id, (ushort)i, (ushort)count,
                    new ReadOnlySpan<byte>(jpg, off, len));
                try { _udp.Send(pkt); } catch { }
            }
            Thread.Sleep(66);
        }
    }

    private void HandleVideo(MediaHeader h, byte[] payload)
    {
        if (!_assemblers.TryGetValue(h.SessionId, out var asm)) _assemblers[h.SessionId] = asm = new FrameAssembler();
        var jpg = asm.Add(h, payload);
        if (jpg == null) return;
        var mat = Cv2.ImDecode(jpg, ImreadModes.Color);
        if (!mat.Empty()) _frames[h.SessionId] = mat;
    }

    // OpenCV windows must be created/updated from a single thread.
    private void DisplayLoop()
    {
        while (_running)
        {
            if (_reset) { _frames.Clear(); Cv2.DestroyAllWindows(); _reset = false; }
            foreach (var kv in _frames)
                Cv2.ImShow(kv.Key == 0 ? "Me" : _nameOf(kv.Key), kv.Value);
            Cv2.WaitKey(30);
        }
        Cv2.DestroyAllWindows();
    }

    // ---------- Receive ----------
    private void ReceiveLoop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            byte[] d;
            try { d = _udp.Receive(ref ep); } catch { if (!_running) break; continue; }
            if (!MediaPacket.TryParse(d, out var h)) continue;
            var payload = d[MediaPacket.HeaderSize..];
            if (h.Type == PacketType.Audio) PlayAudio(h.SessionId, payload);
            else if (h.Type == PacketType.Video) HandleVideo(h, payload);
        }
    }

    public void RemoveSender(int id)
    {
        _frames.TryRemove(id, out _); _assemblers.Remove(id);
        if (_players.Remove(id, out var p)) p.Out.Dispose();
        _reset = true; // recreate windows without the departed one
    }

    public void Reset()
    {
        Active = false;
        foreach (var p in _players.Values) p.Out.Dispose();
        _players.Clear(); _assemblers.Clear(); _reset = true;
    }

    public void Dispose()
    {
        _running = false;
        _mic?.StopRecording(); _mic?.Dispose();
        foreach (var p in _players.Values) p.Out.Dispose();
        _udp.Close();
    }

    /// <summary>Re-assembles UDP fragments of one JPEG frame; drops incomplete/old frames.</summary>
    private sealed class FrameAssembler
    {
        private int _frameId = -1, _received;
        private byte[]?[] _parts = Array.Empty<byte[]?>();

        public byte[]? Add(MediaHeader h, byte[] payload)
        {
            if (h.Seq < _frameId || h.FragCount == 0) return null;
            if (h.Seq != _frameId) { _frameId = h.Seq; _parts = new byte[]?[h.FragCount]; _received = 0; }
            if (h.FragIndex >= _parts.Length || _parts[h.FragIndex] != null) return null;
            _parts[h.FragIndex] = payload;
            if (++_received < _parts.Length) return null;
            return _parts.SelectMany(p => p!).ToArray();
        }
    }
}
