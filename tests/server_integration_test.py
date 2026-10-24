import socket, struct, time
class F:
    def __init__(s,sock): s.s=sock; s.buf=b""
def tcp(name):
    s=socket.create_connection(("127.0.0.1",5000)); s.settimeout(0.6); return s,F(s)
def send(f,l): f.s.sendall((l+"\n").encode())
def rd(f):
    try:
        while True:
            d=f.s.recv(65536)
            if not d: break
            f.buf+=d
    except Exception: pass
    lines=f.buf.decode().split("\n"); f.buf=b""
    return [l.strip() for l in lines if l.strip()]
def hdr(t,sid,seq,i,c): return struct.pack(">BiiHH",t,sid,seq,i,c)
ok=True
def check(name,cond):
    global ok; ok&=bool(cond); print(("PASS " if cond else "FAIL ")+name)

sa,fa=tcp("A"); sb,fb=tcp("B"); sc,fc=tcp("C")
send(fa,"NAME alice"); send(fb,"NAME bob"); send(fc,"NAME carol")
ra,rb,rc=rd(fa),rd(fb),rd(fc); print(ra,rb,rc)
ida=int(ra[0].split()[1]); idb=int(rb[0].split()[1]); idc=int(rc[0].split()[1])
check("WELCOME ids unique",len({ida,idb,idc})==3)

send(fa,"CREATE demo"); send(fa,"CREATE demo"); send(fa,"JOIN demo"); send(fb,"JOIN demo"); send(fb,"ROOMS")
send(fc,"MSG hi"); send(fc,"CREATE other"); send(fc,"JOIN other")
ra,rb,rc=rd(fa),rd(fb),rd(fc); print(ra,rb,rc)
check("create ok / duplicate rejected",ra[0].startswith("OK") and ra[1].startswith("ERR"))
check("A notified bob joined",any(x==f"JOINED {idb} bob" for x in ra))
check("B told about existing member",f"MEMBER {ida} alice" in rb)
check("MSG without room rejected",any(x.startswith("ERR Join") for x in rc))

send(fa,"MSG hello bob"); time.sleep(.3)
ra,rb,rc=rd(fa),rd(fb),rd(fc)
check("chat reaches both in room",f"CHAT alice hello bob" in ra and "CHAT alice hello bob" in rb)
check("chat isolated from other room","CHAT alice hello bob" not in rc)

# UDP
ua=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); ub=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); uc=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
for u in (ua,ub,uc): u.settimeout(1)
for u,i in ((ua,ida),(ub,idb),(uc,idc)): u.sendto(hdr(0,i,0,0,1),("127.0.0.1",5001))
time.sleep(.3)
ua.sendto(hdr(1,ida,1,0,1)+b"AUDIO"*10,("127.0.0.1",5001))
ua.sendto(hdr(2,ida,7,0,2)+b"VIDEOFRAG",("127.0.0.1",5001))
got=[]
try:
    while True: got.append(ub.recv(2000))
except Exception: pass
check("bob receives audio+video from alice",len(got)==2 and got[0][0]==1 and got[1][0]==2 and struct.unpack(">i",got[0][1:5])[0]==ida)
try: uc.recv(2000); check("carol (other room) receives nothing",False)
except Exception: check("carol (other room) receives nothing",True)
try: ua.recv(2000); check("sender does not get echo",False)
except Exception: check("sender does not get echo",True)

# spoofed packet from wrong endpoint
spoof=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); spoof.sendto(hdr(1,ida,2,0,1)+b"X",("127.0.0.1",5001)); time.sleep(.3)
try: ub.recv(2000); check("spoofed packet dropped",False)
except Exception: check("spoofed packet dropped",True)

# leave / delete / owner rules
send(fb,"DELETE demo"); send(fb,"LEAVE"); time.sleep(.4); send(fa,"DELETE demo")
ra,rb=rd(fa),rd(fb); print(ra,rb)
check("non-owner delete rejected",any("Only the room owner" in x for x in rb))
check("LEFT broadcast + ROOM_LEFT",f"LEFT {idb} bob" in ra and "ROOM_LEFT" in rb)
check("owner delete -> ROOMDELETED",any(x.startswith("ROOMDELETED") for x in ra))

# disconnect cleanup + load: 60 clients concurrently
sc.close()
socks=[]
for i in range(60):
    s,f=tcp(str(i)); send(f,f"NAME u{i}"); socks.append((s,f))
send(fb,"CREATE big"); time.sleep(.5)
for s,f in socks: send(f,"JOIN big")
time.sleep(1.5)
for s,f in socks: rd(f)
for s,f in socks: send(f,"MSG ping")
time.sleep(1); n=0
for s,f in socks[:3]:
    n+=sum(1 for x in rd(f) if x.startswith("CHAT"))
check("60 concurrent clients, broadcast works (each of 3 sampled got >=60 msgs)",n>=180)
print("ALL OK" if ok else "SOME FAILED")
