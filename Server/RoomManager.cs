using System.Collections.Concurrent;
namespace Conference.Server;

/// <summary>Creates/deletes rooms and manages joining/leaving (thread-safe).</summary>
public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public (bool Ok, string Msg) Create(string name, ClientSession owner)
    {
        if (string.IsNullOrWhiteSpace(name)) return (false, "Usage: /create <room>");
        return _rooms.TryAdd(name, new Room(name, owner))
            ? (true, $"Room '{name}' created")
            : (false, "Room already exists");
    }

    public async Task<(bool Ok, string Msg)> DeleteAsync(string name, ClientSession by)
    {
        if (!_rooms.TryGetValue(name, out var room)) return (false, "No such room");
        if (room.Owner != by) return (false, "Only the room owner can delete it");
        _rooms.TryRemove(name, out _);
        foreach (var m in room.Snapshot())
        {
            m.Room = null;
            room.Remove(m);
            await m.SendAsync($"ROOMDELETED {room.Name}");
        }
        return (true, $"Room '{name}' deleted");
    }

    public async Task<(bool Ok, string Msg)> JoinAsync(string name, ClientSession s)
    {
        if (!_rooms.TryGetValue(name, out var room)) return (false, "No such room");
        if (s.Room == room) return (false, "Already in this room");
        await LeaveAsync(s);
        room.Add(s);
        s.Room = room;
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
    }

    public string List() => string.Join(",", _rooms.Values.Select(r => $"{r.Name}({r.Snapshot().Length})"));
}
