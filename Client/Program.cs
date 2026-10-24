using System.Collections.Concurrent;
using Conference.Client;
using Conference.Shared;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
Console.Write("Your name: ");
var name = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(name)) name = "user";

var names = new ConcurrentDictionary<int, string>();
MediaClient? media = null;

using var chat = new ChatClient();
await chat.ConnectAsync(host, Protocol.TcpPort);

chat.LineReceived += line =>
{
    var p = line.Split(' ', 3);
    switch (p[0])
    {
        case "WELCOME":
            media = new MediaClient(host, Protocol.UdpPort, int.Parse(p[1]),
                id => names.TryGetValue(id, out var n) ? n : $"user{id}");
            media.Start();
            Console.WriteLine("Connected. Type /help for commands.");
            break;
        case "ROOM_JOINED": if (media != null) media.Active = true; Console.WriteLine($"* Entered room {p[1]}"); break;
        case "ROOM_LEFT":   media?.Reset(); names.Clear(); Console.WriteLine("* Left the room"); break;
        case "ROOMDELETED": media?.Reset(); names.Clear(); Console.WriteLine($"* Room {p[1]} was deleted"); break;
        case "MEMBER": names[int.Parse(p[1])] = p[2]; break;
        case "JOINED": names[int.Parse(p[1])] = p[2]; Console.WriteLine($"* {p[2]} joined"); break;
        case "LEFT":   names.TryRemove(int.Parse(p[1]), out _); media?.RemoveSender(int.Parse(p[1])); Console.WriteLine($"* {p[2]} left"); break;
        case "CHAT":   Console.WriteLine($"[{p[1]}] {(p.Length > 2 ? p[2] : "")}"); break;
        default:       Console.WriteLine(line); break;
    }
};
chat.Disconnected += () => { Console.WriteLine("Disconnected from server."); Environment.Exit(0); };
chat.StartReading();
await chat.SendAsync("NAME " + name);

while (Console.ReadLine() is { } input)
{
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (!input.StartsWith('/')) { await chat.SendAsync("MSG " + input); continue; }

    var parts = input[1..].Split(' ', 2);
    var arg = parts.Length > 1 ? parts[1] : "";
    switch (parts[0].ToLowerInvariant())
    {
        case "create": await chat.SendAsync("CREATE " + arg); break;
        case "delete": await chat.SendAsync("DELETE " + arg); break;
        case "join":   await chat.SendAsync("JOIN " + arg); break;
        case "leave":  await chat.SendAsync("LEAVE"); break;
        case "rooms":  await chat.SendAsync("ROOMS"); break;
        case "mute":   if (media != null) { media.AudioEnabled = !media.AudioEnabled; Console.WriteLine($"Audio {(media.AudioEnabled ? "on" : "muted")}"); } break;
        case "video":  if (media != null) { media.VideoEnabled = !media.VideoEnabled; Console.WriteLine($"Video {(media.VideoEnabled ? "on" : "off")}"); } break;
        case "quit":   await chat.SendAsync("QUIT"); media?.Dispose(); return;
        default:
            Console.WriteLine("/create <room> /delete <room> /join <room> /leave /rooms /mute /video /quit");
            break;
    }
}
