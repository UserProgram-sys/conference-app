# Changelog
## [0.2.0]
- New **WinForms GUI** client (`ClientGui`): room list, chat, participants, video tiles, mic/camera/quality controls.
- Client split into UI-independent `ClientCore` + front-ends (`ClientGui`, `Client`).
- Audio: voice-activity detection, jitter buffer, packet-loss concealment. Video: adaptive quality, resolutions, frame pacing.
- Server: password-protected rooms, automatic removal of empty rooms, unique names, rate limiting, input/size limits,
  UDP session token (anti-hijacking), file logging.
- Integration test extended to 26 checks (all passing).
## [0.1.0]
- Phase 1: solution structure, TCP/UDP sockets.
- Phase 2: rooms + TCP chat.
- Phase 3: UDP audio/video relay with fragmentation.
- Phase 4: README, testing notes.
