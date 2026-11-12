"""Integration test for the conference server. Start the server first, then run:  python3 tests/server_integration_test.py"""
import socket, struct, time, sys

HOST = "127.0.0.1"
ok = True

def check(name, cond):
    global ok
    ok &= bool(cond)
    print(("PASS " if cond else "FAIL ") + name)

class C:
    """Minimal raw-socket TCP client."""
    def __init__(self, name):
        self.s = socket.create_connection((HOST, 5000)); self.s.settimeout(0.2)
        self.buf = b""; self.closed = False
        self.send("NAME " + name)
        p = self.read_until("WELCOME").split(" ", 3)
        self.id, self.token, self.name = int(p[1]), p[2], p[3]
    def send(self, line): self.s.sendall((line + "\n").encode())
    def drain(self):
        try:
            while True:
                d = self.s.recv(65536)
                if not d: self.closed = True; break
                self.buf += d
        except socket.timeout: pass
        except OSError: self.closed = True
        *lines, rest = self.buf.decode(errors="replace").split("\n")
        self.buf = rest.encode()
        return [l.strip() for l in lines if l.strip()]
    def read_until(self, prefix):
        end = time.time() + 3
        while time.time() < end:
            for l in self.drain():
                if l.startswith(prefix): return l
        raise Exception("timeout waiting for " + prefix)

def hdr(t, sid, seq, i, c): return struct.pack(">BiiHH", t, sid, seq, i, c)
def udp(): s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(0.5); return s
def got_any(u):
    try: u.recv(2000); return True
    except Exception: return False

# ---------- identity ----------
a, b, c = C("alice"), C("bob"), C("carol")
a2 = C("alice")
check("unique session ids", len({a.id, b.id, c.id, a2.id}) == 4)
check("duplicate names made unique", a2.name != "alice" and a2.name.startswith("alice"))
check("token issued (16 hex chars)", len(a.token) == 16)
a2.send("QUIT")

# ---------- rooms + chat ----------
a.send("CREATE demo"); a.send("CREATE demo"); a.send("JOIN demo"); b.send("JOIN demo"); b.send("ROOMS")
c.send("MSG hi"); c.send("CREATE other"); c.send("JOIN other")
ra, rb, rc = a.drain(), b.drain(), c.drain()
check("create ok / duplicate rejected", ra[0].startswith("OK") and ra[1].startswith("ERR"))
check("alice notified bob joined", f"JOINED {b.id} bob" in ra)
check("bob told about existing member", f"MEMBER {a.id} alice" in rb)
check("ROOMS lists rooms with counts", any(x.startswith("ROOMS") and "demo(" in x for x in rb))
check("MSG without room rejected", any(x.startswith("ERR Join") for x in rc))
a.send("MSG hello bob"); time.sleep(.3)
ra, rb, rc = a.drain(), b.drain(), c.drain()
check("chat reaches both members", "CHAT alice hello bob" in ra and "CHAT alice hello bob" in rb)
check("chat isolated from other room", "CHAT alice hello bob" not in rc)

# ---------- password rooms ----------
a.send("CREATE secret s3cret pw"); a.drain()
c.send("JOIN secret"); c.send("JOIN secret wrong"); c.send("ROOMS"); time.sleep(.3)
rc = c.drain()
check("join without password rejected", any("Wrong password" in x for x in rc))
check("locked room marked with * in ROOMS", any(x.startswith("ROOMS") and "secret(0)*" in x for x in rc))
c.send("JOIN secret s3cret pw"); time.sleep(.3)
check("join with correct password works", any(x.startswith("ROOM_JOINED secret") for x in c.drain()))
c.send("LEAVE"); c.send("JOIN other"); c.drain()

# ---------- UDP relay + token ----------
ua, ub, uc, spoof = udp(), udp(), udp(), udp()
for u, cl in ((ua, a), (ub, b)): u.sendto(hdr(0, cl.id, 0, 0, 1) + cl.token.encode(), (HOST, 5001))
uc.sendto(hdr(0, c.id, 0, 0, 1) + c.token.encode(), (HOST, 5001))
spoof.sendto(hdr(0, a.id, 0, 0, 1) + b"BADTOKEN0000000", (HOST, 5001))      # hijack attempt
time.sleep(.3)
ua.sendto(hdr(1, a.id, 1, 0, 1) + b"AUDIO" * 10, (HOST, 5001))
ua.sendto(hdr(2, a.id, 7, 0, 2) + b"VIDEOFRAG", (HOST, 5001))
got = []
try:
    while True: got.append(ub.recv(2000))
except Exception: pass
check("bob receives audio+video from alice", len(got) == 2 and got[0][0] == 1 and got[1][0] == 2
      and struct.unpack(">i", got[0][1:5])[0] == a.id)
check("other-room member receives nothing", not got_any(uc))
check("sender gets no echo", not got_any(ua))
check("hijack Hello with wrong token ignored", not got_any(spoof))
spoof.sendto(hdr(1, a.id, 2, 0, 1) + b"X", (HOST, 5001)); time.sleep(.3)
check("spoofed media from unregistered endpoint dropped", not got_any(ub))

# ---------- leave / delete / auto-delete ----------
b.send("DELETE demo"); b.send("LEAVE"); time.sleep(.4); a.send("DELETE demo"); time.sleep(.3)
ra, rb = a.drain(), b.drain()
check("non-owner delete rejected", any("Only the room owner" in x for x in rb))
check("LEFT broadcast + ROOM_LEFT", "ROOM_LEFT" in rb)
check("owner delete -> ROOMDELETED", any(x.startswith("ROOMDELETED") for x in ra))
a.send("CREATE temp"); a.send("JOIN temp"); a.send("LEAVE"); a.send("ROOMS"); time.sleep(.4)
check("empty room removed automatically", not any("temp(" in x for x in a.drain() if x.startswith("ROOMS")))

# ---------- input validation / abuse protection ----------
x = C("flooder"); x.send("CREATE flood"); x.send("JOIN flood"); x.drain()
for _ in range(40): x.send("MSG spam")
time.sleep(.6)
rx = x.drain()
check("chat flood rate-limited", sum(l.startswith("ERR Slow down") for l in rx) > 10 and sum(l.startswith("CHAT") for l in rx) <= 20)
y = C("longline"); y.send("A" * 5000); time.sleep(.5)
ry = y.drain()
check("oversized line rejected and client dropped", any("Line too long" in l for l in ry) or y.closed)

# ---------- load + capacity ----------
host = C("host")
for i in range(3): host.send(f"CREATE r{i}")
host.drain()
clients = [C(f"u{i}") for i in range(60)]
for i, cl in enumerate(clients): cl.send(f"JOIN r{i % 3}")
time.sleep(1.2)
for cl in clients: cl.drain()
extra = C("extra"); extra.send("JOIN r0"); time.sleep(.4)
check("21st member rejected (room capacity 20)", any("Room is full" in l for l in extra.drain()))
for cl in clients: cl.send("MSG ping")
time.sleep(1.5)
counts = [sum(1 for l in cl.drain() if l.startswith("CHAT")) for cl in clients[:3]]
check("60 concurrent clients across 3 rooms, broadcast complete", counts == [20, 20, 20])

print("ALL OK" if ok else "SOME FAILED"); sys.exit(0 if ok else 1)
