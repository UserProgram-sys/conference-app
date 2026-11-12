using Conference.Client;

// Lightweight console client: chat + audio. (Use ClientGui for video.)
var host = args.Length > 0 ? args[0] : "127.0.0.1";
Console.Write("Your name: ");
var name = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(name)) name = "user";

using var session = new ConferenceSession();
bool showRooms = false;

session.Connected += () =>
{
    session.Media!.VideoEnabled = false;       // no video windows in the console client
    Console.WriteLine($"Connected as {session.MyName}. Type /help for commands.");
};
session.ServerMessage += (m, err) => Console.WriteLine(err ? $"! {m}" : m);
session.ChatReceived += (u, t) => Console.WriteLine($"[{u}] {t}");
session.RoomJoined += r => Console.WriteLine($"* Entered room {r}");
session.RoomLeft += why => Console.WriteLine($"* {why}");
session.MemberJoined += (_, n, existing) => { if (!existing) Console.WriteLine($"* {n} joined"); };
session.MemberLeft += (_, n) => Console.WriteLine($"* {n} left");
session.RoomsUpdated += rooms =>
{
    if (!showRooms) return;
    showRooms = false;
    Console.WriteLine(rooms.Count == 0 ? "No rooms." : "Rooms: " + string.Join(", ", rooms.Select(r => $"{r.Name}({r.Members}){(r.Locked ? " [password]" : "")}")));
};
session.Disconnected += () => { Console.WriteLine("Disconnected from server."); Environment.Exit(0); };

await session.ConnectAsync(host, name);

while (Console.ReadLine() is { } input)
{
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (!input.StartsWith('/')) { await session.SendChatAsync(input); continue; }

    var parts = input[1..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
    var room = parts.Length > 1 ? parts[1] : "";
    var pass = parts.Length > 2 ? parts[2] : null;
    switch (cmd)
    {
        case "create": await session.CreateRoomAsync(room, pass); break;
        case "join":   await session.JoinRoomAsync(room, pass); break;
        case "delete": await session.DeleteRoomAsync(room); break;
        case "leave":  await session.LeaveRoomAsync(); break;
        case "rooms":  showRooms = true; await session.RefreshRoomsAsync(); break;
        case "mute":
            if (session.Media != null) { session.Media.AudioEnabled = !session.Media.AudioEnabled; Console.WriteLine($"Audio {(session.Media.AudioEnabled ? "on" : "muted")}"); }
            break;
        case "quit": return;
        default:
            Console.WriteLine("/create <room> [password]  /join <room> [password]  /leave  /delete <room>  /rooms  /mute  /quit");
            break;
    }
}
