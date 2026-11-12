using System.Collections.Concurrent;
using Conference.Server;
using Conference.Shared;

var sessions = new ConcurrentDictionary<int, ClientSession>();
var rooms = new RoomManager();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Streaming gets its own thread so chat load never delays media.
new Thread(() => new UdpMediaRelay(sessions).Run(Protocol.UdpPort, cts.Token))
{ IsBackground = true, Name = "UDP-Relay" }.Start();

await new TcpChatServer(rooms, sessions).RunAsync(Protocol.TcpPort, cts.Token);
