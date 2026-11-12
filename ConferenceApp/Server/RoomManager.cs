using System.Collections.Concurrent;
using Conference.Shared;
namespace Conference.Server;

/// <summary>Creates/deletes rooms and manages joining/leaving (thread-safe).</summary>
public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public (bool Ok, string Msg) Create(string name, string? password, ClientSession owner)
    {
        if (string.IsNullOrWhiteSpace(name)) return (false, "Usage: create <room> [password]");
        if (_rooms.Count >= Limits.MaxRooms) return (false, "Too many rooms");
        if (password is { Length: > Limits.MaxPasswordLength }) password = password[..Limits.MaxPasswordLength];
        var room = new Room(name, owner, password);
        return _rooms.TryAdd(name, room)
            ? (true, $"Room '{name}' created{(room.Locked ? " (password protected)" : "")}")
            : (false, "Room already exists");
    }

    public async Task<(bool Ok, string Msg)> DeleteAsync(string name, ClientSession by)
    {
        if (!_rooms.TryGetValue(name, out var room)) return (false, "No such room");
        if (room.Owner != by) return (false, "Only the room owner can delete it");
        _rooms.TryRemove(new KeyValuePair<string, Room>(room.Name, room));
        foreach (var m in room.Snapshot())
        {
            m.Room = null;
            room.Remove(m);
            await m.SendAsync($"ROOMDELETED {room.Name}");
        }
        return (true, $"Room '{name}' deleted");
    }

    public async Task<(bool Ok, string Msg)> JoinAsync(string name, string? password, ClientSession s)
    {
        if (!_rooms.TryGetValue(name, out var room)) return (false, "No such room");
        if (s.Room == room) return (false, "Already in this room");
        if (!room.CheckPassword(password)) return (false, "Wrong password");
        if (room.Count >= Limits.MaxRoomMembers) return (false, "Room is full");
        await LeaveAsync(s);
        room.Add(s);
        s.Room = room;
        if (!_rooms.TryGetValue(room.Name, out var current) || current != room)   // closed while joining
        {
            room.Remove(s); s.Room = null;
            return (false, "Room was closed");
        }
        foreach (var m in room.Snapshot())                       // tell the newcomer who is here
            if (m != s) await s.SendAsync($"MEMBER {m.Id} {m.Name}");
        await room.BroadcastAsync($"JOINED {s.Id} {s.Name}", except: s);
        await s.SendAsync($"ROOM_JOINED {room.Name}");
        return (true, $"Joined '{room.Name}'");
    }

    public async Task LeaveAsync(ClientSession s)
    {
        var room = s.Room;
        if (room == null) return;
        room.Remove(s);
        s.Room = null;
        await room.BroadcastAsync($"LEFT {s.Id} {s.Name}");
        if (room.Count == 0)                                     // empty rooms are removed automatically
            _rooms.TryRemove(new KeyValuePair<string, Room>(room.Name, room));
    }

    /// <summary>"name(count)" per room, a trailing * marks password-protected rooms.</summary>
    public string List() => string.Join(",", _rooms.Values.Select(r => $"{r.Name}({r.Count}){(r.Locked ? "*" : "")}"));
}
