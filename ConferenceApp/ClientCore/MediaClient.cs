using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Conference.Shared;
using NAudio.Wave;
using OpenCvSharp;
namespace Conference.Client;

public enum VideoProfile { Low, Medium, High }   // 320x240, 480x360, 640x480

/// <summary>
/// UDP side of the client (audio + video). It has NO UI code: decoded/received video frames are
/// handed to the UI through the <see cref="VideoFrame"/> event as JPEG bytes (sender 0 = local preview).
///
/// Quality features
///  * Audio: 16 kHz mono, 20 ms packets, silence suppression (voice activity detection),
///    per-sender jitter buffer with pre-buffering, duplicate/late packet dropping and
///    packet-loss concealment (short gaps are filled with silence).
///  * Video: adaptive JPEG quality keeps every frame between ~8 and ~20 KB, selectable resolution,
///    steady frame pacing (~15 fps) and fragment re-assembly that drops incomplete frames.
/// </summary>
public sealed class MediaClient : IDisposable
{
    private static readonly WaveFormat Format = new(16000, 16, 1);
    private const int PrimePackets = 3;          // start playback after 60 ms are buffered
    private const int MaxConcealGap = 5;         // fill gaps up to 100 ms with silence
    private const double VoiceThreshold = 250;   // RMS level treated as speech
    private const int VoiceHangPackets = 10;     // keep sending 200 ms after speech stops
    private const int FrameIntervalMs = 66;      // ~15 fps
    private const int TargetMinBytes = 8000, TargetMaxBytes = 20000;

    private readonly UdpClient _udp = new();
    private readonly string _host; private readonly int _port;
    private readonly int _sessionId; private readonly byte[] _token;
    private readonly object _sync = new();
    private readonly Dictionary<int, Player> _players = new();
    private readonly Dictionary<int, FrameAssembler> _assemblers = new();
    private volatile bool _running;
    private WaveInEvent? _mic;
    private int _audioSeq, _frameId, _hang;

    public volatile bool Active;                // true only while inside a room
    public volatile bool AudioEnabled = true;
    public volatile bool VideoEnabled = true;
    public volatile VideoProfile Profile = VideoProfile.Low;

    /// <summary>A JPEG frame arrived (senderId 0 = our own preview).</summary>
    public event Action<int, byte[]>? VideoFrame;
    public event Action<string>? Warning;
    /// <summary>Raised when the client itself changes a setting (e.g. camera missing -> video off).</summary>
    public event Action? SettingsChanged;

    public MediaClient(string host, int port, int sessionId, string token)
    { _host = host; _port = port; _sessionId = sessionId; _token = Encoding.ASCII.GetBytes(token); }

    public void Start()
    {
        _udp.Connect(_host, _port);
        _running = true;
        SendHello();
        Spawn(ReceiveLoop, "UDP-Recv");
        Spawn(KeepAliveLoop, "UDP-KeepAlive");
        Spawn(VideoSendLoop, "Video-Send");
        StartMicrophone();
    }

    private static void Spawn(ThreadStart a, string n) => new Thread(a) { IsBackground = true, Name = n }.Start();

    private void SendHello() => _udp.Send(MediaPacket.Build(PacketType.Hello, _sessionId, 0, 0, 1, _token));

    private void KeepAliveLoop()
    {
        while (_running) { try { SendHello(); } catch { } Thread.Sleep(5000); }
    }

    // ================= Audio =================
    private void StartMicrophone()
    {
        try
        {
            _mic = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 20 };
            _mic.DataAvailable += (_, e) =>
            {
                int seq = Interlocked.Increment(ref _audioSeq);          // counts every 20 ms, even when silent
                if (!Active || !AudioEnabled || e.BytesRecorded == 0) return;

                // silence suppression: only transmit while someone is speaking (+ short hang time)
                if (Rms(e.Buffer, e.BytesRecorded) > VoiceThreshold) _hang = VoiceHangPackets;
                else if (_hang > 0) _hang--;
                else return;

                var pkt = MediaPacket.Build(PacketType.Audio, _sessionId, seq, 0, 1,
                    new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));
                try { _udp.Send(pkt); } catch { }
            };
            _mic.StartRecording();
        }
        catch (Exception ex)
        {
            AudioEnabled = false;
            Warning?.Invoke($"Microphone unavailable: {ex.Message}");
            SettingsChanged?.Invoke();
        }
    }

    private static double Rms(byte[] b, int n)
    {
        int samples = n / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(b[2 * i] | (b[2 * i + 1] << 8));
            sum += (double)s * s;
        }
        return Math.Sqrt(sum / samples);
    }

    private void PlayAudio(int sender, int seq, byte[] pcm)
    {
        lock (_sync)
        {
            if (!_players.TryGetValue(sender, out var p))
            {
                var buf = new BufferedWaveProvider(Format)
                { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(300) };
                var wo = new WaveOutEvent { DesiredLatency = 100 };
                wo.Init(buf);
                _players[sender] = p = new Player(buf, wo);
            }

            if (p.LastSeq != 0)
            {
                if (seq <= p.LastSeq && p.LastSeq - seq < 1000) return;          // late or duplicate packet
                int gap = seq - p.LastSeq - 1;
                if (gap > 0 && gap <= MaxConcealGap)                            // packet-loss concealment
                {
                    var silence = new byte[gap * pcm.Length];
                    p.Buf.AddSamples(silence, 0, silence.Length);
                }
            }
            p.LastSeq = seq;
            p.Buf.AddSamples(pcm, 0, pcm.Length);

            if (!p.Started && p.Buf.BufferedBytes >= PrimePackets * pcm.Length)  // jitter-buffer pre-fill
            {
                p.Out.Play();
                p.Started = true;
            }
        }
    }

    // ================= Video =================
    private static (int W, int H) SizeOf(VideoProfile p) => p switch
    {
        VideoProfile.High => (640, 480),
        VideoProfile.Medium => (480, 360),
        _ => (320, 240)
    };

    private void VideoSendLoop()
    {
        VideoCapture? cam = null;
        int quality = 60;
        var lastProfile = Profile;
        var clock = new Stopwatch();

        while (_running)
        {
            clock.Restart();
            if (!Active || !VideoEnabled)                 // release the camera when not needed
            {
                cam?.Dispose(); cam = null;
                Thread.Sleep(100);
                continue;
            }
            if (cam == null)
            {
                cam = new VideoCapture(0);
                if (!cam.IsOpened())
                {
                    cam.Dispose(); cam = null;
                    VideoEnabled = false;
                    Warning?.Invoke("No camera found - video sending disabled.");
                    SettingsChanged?.Invoke();
                    continue;
                }
            }
            if (Profile != lastProfile) { lastProfile = Profile; quality = 60; }

            using var frame = new Mat();
            if (!cam.Read(frame) || frame.Empty()) { Thread.Sleep(50); continue; }

            var (w, h) = SizeOf(lastProfile);
            using var small = new Mat();
            Cv2.Resize(frame, small, new OpenCvSharp.Size(w, h));
            Cv2.ImEncode(".jpg", small, out var jpg, new[] { (int)ImwriteFlags.JpegQuality, quality });

            // adaptive quality: keep each frame within the target size band
            if (jpg.Length > TargetMaxBytes && quality > 30) quality -= 5;
            else if (jpg.Length < TargetMinBytes && quality < 85) quality += 2;

            VideoFrame?.Invoke(0, jpg);                               // local preview

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

            int wait = FrameIntervalMs - (int)clock.ElapsedMilliseconds;   // steady frame pacing
            if (wait > 0) Thread.Sleep(wait);
        }
        cam?.Dispose();
    }

    private void HandleVideo(MediaHeader h, byte[] payload)
    {
        FrameAssembler asm;
        lock (_sync)
        {
            if (!_assemblers.TryGetValue(h.SessionId, out asm!)) _assemblers[h.SessionId] = asm = new FrameAssembler();
        }
        var jpg = asm.Add(h, payload);
        if (jpg != null) VideoFrame?.Invoke(h.SessionId, jpg);
    }

    // ================= Receive =================
    private void ReceiveLoop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            byte[] d;
            try { d = _udp.Receive(ref ep); } catch { if (!_running) break; continue; }
            if (!MediaPacket.TryParse(d, out var h)) continue;
            var payload = d[MediaPacket.HeaderSize..];
            if (h.Type == PacketType.Audio) PlayAudio(h.SessionId, h.Seq, payload);
            else if (h.Type == PacketType.Video) HandleVideo(h, payload);
        }
    }

    public void RemoveSender(int id)
    {
        lock (_sync)
        {
            _assemblers.Remove(id);
            if (_players.Remove(id, out var p)) p.Out.Dispose();
        }
    }

    public void Reset()
    {
        Active = false;
        lock (_sync)
        {
            foreach (var p in _players.Values) p.Out.Dispose();
            _players.Clear();
            _assemblers.Clear();
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
        Reset();
        _udp.Close();
    }

    private sealed class Player
    {
        public Player(BufferedWaveProvider buf, WaveOutEvent @out) { Buf = buf; Out = @out; }
        public BufferedWaveProvider Buf { get; }
        public WaveOutEvent Out { get; }
        public int LastSeq;
        public bool Started;
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
