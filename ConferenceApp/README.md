# Real-Time Conference Application (ISE_F25_NAP_C1)

.NET 8 / C# conference system: **TCP text chat** + **UDP audio/video** relayed by a multithreaded server,
with a **WinForms GUI** client and a lightweight console client.

## Structure
| Project | Role |
|---|---|
| `Shared` | Ports, safety limits, rate limiter, UDP packet format (`MediaPacket`) |
| `Server` | Room management (passwords, auto-delete), TCP chat server, UDP media relay, logging |
| `ClientCore` | UI-independent client library: `ChatClient` (TCP), `MediaClient` (UDP audio/video), `ConferenceSession` (protocol + events) |
| `ClientGui` | **WinForms** client: room list, chat, participants list, live video tiles, mic/camera/quality controls |
| `Client` | Console client (chat + audio) |
| `tests` | Python integration test for the server |

Network code (`ClientCore`, `Server`) is completely separated from UI code (`ClientGui`, `Client`).

## Architecture
- **TCP :5000** - line protocol: `NAME`, `ROOMS`, `CREATE room [pass]`, `JOIN room [pass]`, `DELETE`, `LEAVE`, `MSG`, `QUIT`.
  Server replies: `WELCOME id token name`, `OK/ERR`, `ROOMS name(count)[*]`, `ROOM_JOINED`, `ROOM_LEFT`, `ROOMDELETED`,
  `MEMBER/JOINED/LEFT`, `CHAT user text`.
- **UDP :5001** - 13-byte header `[type][sessionId][seq][fragIdx][fragCount]` + payload.
  A `Hello` packet (+ keep-alives) carrying the **secret token** from `WELCOME` registers the client's UDP endpoint;
  the server forwards audio/video only to *other members of the same room*.
- **Threads**: one async task per TCP client; a dedicated thread for the UDP relay; on the client separate threads
  for UDP receive, keep-alive, video capture and audio capture. State is thread-safe
  (`ConcurrentDictionary`, locks, a send lock per connection).

## Quality of audio / video
- **Audio** (16 kHz mono, 20 ms packets): silence suppression (voice-activity detection), per-speaker jitter buffer with
  pre-buffering, late/duplicate packet dropping and packet-loss concealment.
- **Video**: adaptive JPEG quality (each frame kept between ~8 and ~20 KB), selectable resolution (320x240 / 480x360 / 640x480),
  steady ~15 fps pacing, fragment re-assembly that drops incomplete frames; the camera is released when video is off or you leave the room.

## Security & stability
- Rooms can be **password protected** (stored as SHA-256 hash, compared in constant time).
- UDP `Hello` needs a random per-session **token** - nobody can hijack another participant's media stream.
- Media is accepted only from the registered endpoint; **rate limiting** for chat (5 msg/s) and UDP (flood protection).
- Limits: 200 clients, 100 rooms, 20 members per room, 1000-char lines (longer lines disconnect the client), unique user names,
  sanitised names; empty rooms are removed automatically; events are logged to `logs/server-yyyyMMdd.log`.

## Build & Run
Requirements: .NET 8 SDK, **Windows** for the clients (NAudio + OpenCvSharp Windows runtime), webcam + microphone.
The server runs on any OS.
```bash
dotnet build ConferenceApp.sln
dotnet run --project Server                  # TCP 5000, UDP 5001
dotnet run --project ClientGui               # GUI client
dotnet run --project ClientGui               # start it again for a second participant
dotnet run --project Client -- 192.168.1.10  # console client (optional server address)
```
Open the firewall for TCP 5000 and UDP 5001 on the server machine. To use it over the Internet without port forwarding, connect
the machines with a VPN such as Tailscale and use the server's VPN address.

### GUI quick test
1. Start the server. 2. Start two `ClientGui` windows. 3. Enter the server address and a name, press **Connect**.
4. Client A: **Create** (room name, optional password), select the room, **Join**. 5. Client B: double-click the room (enter the password).
6. Chat, see each other's video tiles and talk (use headphones to avoid echo). Use the **Mic / Camera / Video quality** controls.

### Console client commands
`/create <room> [password]` `/join <room> [password]` `/leave` `/delete <room>` `/rooms` `/mute` `/quit`; other text is chat.

## Third-party packages
NAudio 2.2.1, OpenCvSharp4 4.9.0 (+ `OpenCvSharp4.runtime.win`). WinForms is part of .NET (Windows).

## Team members & contributions
Course: Network Application Programming (ISE_F25_NAP_C1) – Syrian Virtual University

| Member | Student ID | Contribution |
|---|---|---|
| Ahmad Fouad Sate' (احمد فؤاد ساطع) | ahmadfouad_13781 | Server: room management (`RoomManager`, `Room`, `ClientSession`) |
| George Kabrsli (جورج قبرصلي) | George_89266 | Server: TCP chat server (`TcpChatServer`), command protocol |
| Mohammad Ghaleb Nasri (محمد غالب نصري) | Mhd.ghaleb_28497 | Server: UDP media relay (`UdpMediaRelay`), shared packet format, multithreading |
| Yasen Alalabi (ياسين العلبي) | Yasen_96832 | Client: console UI, `ChatClient` (TCP), audio capture/playback (NAudio) |
| Khaled Arar (خالد عرار) | Khaled_84196 | Client: video capture/fragmentation/display (OpenCvSharp), testing, README/CHANGELOG |
| ahmad Alfayome (احمد الفيومي) | ahmad_21895 | 
> Update this table to match what each member actually did (see the commit history).

## Testing
`tests/server_integration_test.py` (Python 3, no dependencies) drives a running server with raw TCP/UDP sockets and checks:
rooms (create/duplicate/delete/owner rule), chat isolation between rooms, UDP audio/video relay only inside the room,
no echo to sender, spoofed-endpoint packets dropped, and 60 concurrent clients broadcasting.
```bash
dotnet run --project Server &
python3 tests/server_integration_test.py   # prints PASS/FAIL per check
```
