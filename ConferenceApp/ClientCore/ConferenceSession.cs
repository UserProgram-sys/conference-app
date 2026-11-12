using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Conference.Shared;
namespace Conference.Client;

public sealed record RoomInfo(string Name, int Members, bool Locked);

/// <summary>
/// Everything a UI needs: owns the TCP chat connection and the UDP media client, parses the
/// server protocol and exposes it as events. Used by both the WinForms GUI and the console client.
/// Events are raised on background threads - UIs must marshal to their own thread.
/// </summary>
public sealed class ConferenceSession : IDisposable
{
    private ChatClient? _chat;
    private string _host = "";

    public MediaClient? Media { get; private set; }
    public ConcurrentDictionary<int, string> Members { get; } = new();
    public string? CurrentRoom { get; private set; }
    public int MyId { get; private set; }
    public string MyName { get; private set; } = "";

    public event Action? Connected;                          // WELCOME received, Media is ready
    public event Action<IReadOnlyList<RoomInfo>>? RoomsUpdated;
    public event Action<string>? RoomJoined;
    public event Action<string>? RoomLeft;                   // reason text
    public event Action<int, string, bool>? MemberJoined;   // id, name, alreadyInRoom
    public event Action<int, string>? MemberLeft;
    public event Action<string, string>? ChatReceived;       // user, text
    public event Action<string, bool>? ServerMessage;        // text, isError
    public event Action? Disconnected;

    public async Task ConnectAsync(string host, string name)
    {
        Disconnect();
        _host = host;
        var chat = new ChatClient();
        await chat.ConnectAsync(host, Protocol.TcpPort);
        chat.LineReceived += OnLine;
        chat.Disconnected += () => { if (ReferenceEquals(_chat, chat)) Disconnected?.Invoke(); };
        _chat = chat;
        chat.StartReading();
        await chat.SendAsync("NAME " + name);
    }

    public void Disconnect()
    {
        var chat = _chat; _chat = null;
        try { chat?.SendAsync("QUIT").Wait(500); } catch { }
        chat?.Dispose();
        Media?.Dispose(); Media = null;
        Members.Clear(); CurrentRoom = null;
    }

    public string NameOf(int id) => Members.TryGetValue(id, out var n) ? n : $"user{id}";

    // ---- commands ----
    private Task Send(string line) => _chat?.SendAsync(line) ?? Task.CompletedTask;
    private static string Clean(string room) => room.Replace(" ", "");

    public Task RefreshRoomsAsync() => Send("ROOMS");
    public Task CreateRoomAsync(string room, string? password = null) =>
        Send($"CREATE {Clean(room)}" + (string.IsNullOrEmpty(password) ? "" : " " + password));
    public Task JoinRoomAsync(string room, string? password = null) =>
        Send($"JOIN {Clean(room)}" + (string.IsNullOrEmpty(password) ? "" : " " + password));
    public Task LeaveRoomAsync() => Send("LEAVE");
    public Task DeleteRoomAsync(string room) => Send("DELETE " + Clean(room));
    public Task SendChatAsync(string text) => Send("MSG " + text);

    // ---- protocol parsing ----
    private static readonly Regex RoomRx = new(@"^(.+)\((\d+)\)(\*?)$", RegexOptions.Compiled);

    private void OnLine(string line)
    {
        var p = line.Split(' ', 4);
        switch (p[0])
        {
            case "WELCOME":   // WELCOME <id> <token> <assignedName>
                MyId = int.Parse(p[1]);
                MyName = p.Length > 3 ? p[3] : "";
                Media?.Dispose();
                Media = new MediaClient(_host, Protocol.UdpPort, MyId, p[2]);
                Media.Start();
                Connected?.Invoke();
                _ = RefreshRoomsAsync();
                break;
            case "ROOMS":
                var list = new List<RoomInfo>();
                foreach (var item in (p.Length > 1 ? line[6..] : "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var m = RoomRx.Match(item.Trim());
                    if (m.Success) list.Add(new RoomInfo(m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value == "*"));
                }
                RoomsUpdated?.Invoke(list);
                break;
            case "ROOM_JOINED":
                CurrentRoom = p[1];
                Members.Clear();
                if (Media != null) Media.Active = true;
                RoomJoined?.Invoke(p[1]);
                _ = RefreshRoomsAsync();
                break;
            case "ROOM_LEFT": LeaveCleanup("You left the room"); break;
            case "ROOMDELETED": LeaveCleanup($"Room {p[1]} was deleted"); break;
            case "MEMBER":
                Members[int.Parse(p[1])] = string.Join(' ', p.Skip(2));
                MemberJoined?.Invoke(int.Parse(p[1]), Members[int.Parse(p[1])], true);
                break;
            case "JOINED":
                Members[int.Parse(p[1])] = string.Join(' ', p.Skip(2));
                MemberJoined?.Invoke(int.Parse(p[1]), Members[int.Parse(p[1])], false);
                _ = RefreshRoomsAsync();
                break;
            case "LEFT":
                Members.TryRemove(int.Parse(p[1]), out _);
                Media?.RemoveSender(int.Parse(p[1]));
                MemberLeft?.Invoke(int.Parse(p[1]), string.Join(' ', p.Skip(2)));
                _ = RefreshRoomsAsync();
                break;
            case "CHAT":
                ChatReceived?.Invoke(p.Length > 1 ? p[1] : "?", p.Length > 3 ? p[2] + " " + p[3] : p.Length > 2 ? p[2] : "");
                break;
            case "OK":
                ServerMessage?.Invoke(line[3..], false);
                _ = RefreshRoomsAsync();
                break;
            case "ERR":
                ServerMessage?.Invoke(line.Length > 4 ? line[4..] : "Error", true);
                break;
            default:
                ServerMessage?.Invoke(line, false);
                break;
        }
    }

    private void LeaveCleanup(string reason)
    {
        Media?.Reset();
        Members.Clear();
        CurrentRoom = null;
        RoomLeft?.Invoke(reason);
        _ = RefreshRoomsAsync();
    }

    public void Dispose() => Disconnect();
}
