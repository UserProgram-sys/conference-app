# Real-Time Conference Application (ISE_F25_NAP)

.NET 8 / C# conference system: **TCP text chat** + **UDP audio/video** relayed by a multithreaded server.

## Structure
| Project | Role |
|---|---|
| `Shared` | Ports + UDP packet format (`MediaPacket`) |
| `Server` | Room management, TCP chat server, UDP media relay |
| `Client` | Console client (chat) + NAudio microphone/speaker + OpenCvSharp camera/preview windows |

## Architecture
- **TCP :5000** – line protocol: `NAME`, `ROOMS`, `CREATE`, `DELETE`, `JOIN`, `LEAVE`, `MSG`, `QUIT`.
  Server replies: `WELCOME id`, `OK/ERR`, `ROOM_JOINED`, `ROOM_LEFT`, `ROOMDELETED`, `MEMBER/JOINED/LEFT`, `CHAT user text`.
- **UDP :5001** – 13-byte header `[type][sessionId][seq][fragIdx][fragCount]` + payload.
  The client sends a `Hello` packet (and keep-alives) so the server learns its UDP endpoint; the server forwards
  audio/video only to *other members of the same room*.
- **Audio**: 16 kHz, 16-bit, mono PCM, 20 ms per packet. **Video**: 320x240 JPEG (~15 fps) split into ≤1200-byte fragments;
  incomplete frames are dropped (no retransmission, as expected with UDP).
- **Threads**: one async task per TCP client; a dedicated thread for the UDP relay; on the client separate threads
  for receive, keep-alive, video capture and display. Room/session state is thread-safe (`ConcurrentDictionary`, locks, send lock per client).

## Build & Run
Requirements: .NET 8 SDK, Windows (client uses NAudio WinMM + OpenCvSharp Windows runtime), webcam + microphone.
```bash
dotnet run --project Server                 # start server (TCP 5000, UDP 5001)
dotnet run --project Client                 # client on same machine
dotnet run --project Client -- 192.168.1.10 # client connecting to a remote server
```
Open firewall for TCP 5000 and UDP 5001 on the server.

### Client commands
`/create <room>` `/delete <room>` `/join <room>` `/leave` `/rooms` `/mute` `/video` `/quit` – any other text is sent as chat.

### Quick test
1. Start the server. 2. Start 2+ clients. 3. Client A: `/create demo`, `/join demo`. 4. Client B: `/join demo`.
5. Type messages; camera windows and audio appear for each remote participant. Use headphones to avoid echo.

## Third-party packages
NAudio 2.2.1, OpenCvSharp4 4.9.0 (+ `OpenCvSharp4.runtime.win`).

## Team members & contributions
Course: Network Application Programming (ISE_F25_NAP_C1) – Syrian Virtual University

| Member | Student ID | Contribution |
|---|---|---|
| Ahmad Fouad Sate' (احمد فؤاد ساطع) | ahmadfouad_13781 | Server: room management (`RoomManager`, `Room`, `ClientSession`) |
| George Kabrsli (جورج قبرصلي) | George_89266 | Server: TCP chat server (`TcpChatServer`), command protocol |
| Mohammad Ghaleb Nasri (محمد غالب نصري) | Mhd.ghaleb_28497 | Server: UDP media relay (`UdpMediaRelay`), shared packet format, multithreading |
| Yasen Alalabi (ياسين العلبي) | Yasen_96832 | Client: console UI, `ChatClient` (TCP), audio capture/playback (NAudio) |
| Khaled Arar (خالد عرار) | Khaled_84196 | Client: video capture/fragmentation/display (OpenCvSharp), testing, README/CHANGELOG |

> Update this table to match what each member actually did (see the commit history).

## Testing
`tests/server_integration_test.py` (Python 3, no dependencies) drives a running server with raw TCP/UDP sockets and checks:
rooms (create/duplicate/delete/owner rule), chat isolation between rooms, UDP audio/video relay only inside the room,
no echo to sender, spoofed-endpoint packets dropped, and 60 concurrent clients broadcasting.
```bash
dotnet run --project Server &
python3 tests/server_integration_test.py   # prints PASS/FAIL per check
```
