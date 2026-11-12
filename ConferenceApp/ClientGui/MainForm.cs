using System.Collections.Concurrent;
using Conference.Client;

namespace Conference.ClientGui;

/// <summary>
/// WinForms front-end. All networking lives in ClientCore (ConferenceSession / MediaClient);
/// this class only builds the UI and reacts to events (marshalled to the UI thread with Ui()).
/// </summary>
public sealed class MainForm : Form
{
    private readonly ConferenceSession _s = new();

    // connection bar
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 140 };
    private readonly TextBox _name = new() { Text = Environment.UserName, Width = 120 };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly Label _status = new() { Text = "Not connected", AutoSize = true, Margin = new Padding(12, 7, 0, 0) };

    // rooms
    private readonly ListBox _rooms = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _create = new() { Text = "Create", AutoSize = true };
    private readonly Button _join = new() { Text = "Join", AutoSize = true };
    private readonly Button _delete = new() { Text = "Delete", AutoSize = true };
    private List<RoomInfo> _roomInfos = new();

    // participants, video, chat
    private readonly ListBox _members = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly FlowLayoutPanel _videoPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(30, 30, 30) };
    private readonly RichTextBox _chat = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White };
    private readonly TextBox _chatInput = new() { Dock = DockStyle.Fill };
    private readonly Button _send = new() { Text = "Send", Dock = DockStyle.Right, Width = 70 };

    // bottom bar
    private readonly Button _mic = new() { Text = "Mic: ON", AutoSize = true };
    private readonly Button _cam = new() { Text = "Camera: ON", AutoSize = true };
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Button _leave = new() { Text = "Leave room", AutoSize = true };

    private readonly ConcurrentDictionary<int, byte[]> _latest = new();    // newest JPEG per participant
    private readonly Dictionary<int, (Panel Tile, PictureBox Pic)> _tiles = new();
    private readonly System.Windows.Forms.Timer _videoTimer = new() { Interval = 66 };
    private VideoProfile _profile = VideoProfile.Low;
    private bool _connected;

    public MainForm()
    {
        Text = "Conference";
        Width = 1150; Height = 720; MinimumSize = new Size(950, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        WireEvents();
        UpdateUi();
        _videoTimer.Tick += OnVideoTick;
        _videoTimer.Start();
    }

    // ------------------------------------------------------------------ layout
    private void BuildLayout()
    {
        // center: video on top (55%), chat below (45%)
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var chatPanel = new Panel { Dock = DockStyle.Fill };
        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        inputRow.Controls.Add(_chatInput);       // Fill control first, then the docked-right button
        inputRow.Controls.Add(_send);
        chatPanel.Controls.Add(_chat);
        chatPanel.Controls.Add(inputRow);
        center.Controls.Add(_videoPanel, 0, 0);
        center.Controls.Add(chatPanel, 0, 1);

        // left: rooms
        var left = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(6) };
        var roomButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
        roomButtons.Controls.AddRange(new Control[] { _create, _join, _delete });
        left.Controls.Add(_rooms);
        left.Controls.Add(new Label { Text = "Rooms (double-click to join)", Dock = DockStyle.Top, Height = 20 });
        left.Controls.Add(roomButtons);

        // right: participants
        var right = new Panel { Dock = DockStyle.Right, Width = 170, Padding = new Padding(6) };
        right.Controls.Add(_members);
        right.Controls.Add(new Label { Text = "Participants", Dock = DockStyle.Top, Height = 20 });

        // top: connection bar
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 0, 0) };
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "Server:", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, _host,
            new Label { Text = "Name:", AutoSize = true, Margin = new Padding(8, 7, 0, 0) }, _name, _connect, _status
        });

        // bottom: media controls
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6, 4, 0, 0) };
        bottom.Controls.AddRange(new Control[]
        {
            _mic, _cam,
            new Label { Text = "Video quality:", AutoSize = true, Margin = new Padding(12, 7, 0, 0) }, _quality, _leave
        });
        _quality.Items.AddRange(new object[] { "Low (320x240)", "Medium (480x360)", "High (640x480)" });
        _quality.SelectedIndex = 0;

        // WinForms docks the LAST added control first, so the Fill control goes in first.
        Controls.Add(center);
        Controls.Add(left);
        Controls.Add(right);
        Controls.Add(top);
        Controls.Add(bottom);
    }

    // ------------------------------------------------------------------ events
    private void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (ObjectDisposedException) { }
    }

    private void WireEvents()
    {
        _s.Connected += () => Ui(OnConnected);
        _s.Disconnected += () => Ui(OnDisconnected);
        _s.RoomsUpdated += r => Ui(() => ShowRooms(r));
        _s.RoomJoined += name => Ui(() => OnRoomJoined(name));
        _s.RoomLeft += why => Ui(() => OnRoomLeft(why));
        _s.MemberJoined += (id, nm, existing) => Ui(() =>
        {
            AddTile(id, nm); RefreshMembers();
            if (!existing) Append($"* {nm} joined");
        });
        _s.MemberLeft += (id, nm) => Ui(() => { RemoveTile(id); RefreshMembers(); Append($"* {nm} left"); });
        _s.ChatReceived += (u, t) => Ui(() => Append($"[{u}] {t}"));
        _s.ServerMessage += (m, err) => Ui(() => Append(err ? $"! {m}" : m));

        _connect.Click += async (_, _) => await ToggleConnectionAsync();
        _create.Click += async (_, _) => await CreateRoomAsync();
        _join.Click += async (_, _) => await JoinSelectedAsync();
        _rooms.DoubleClick += async (_, _) => await JoinSelectedAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _leave.Click += async (_, _) => await _s.LeaveRoomAsync();
        _send.Click += async (_, _) => await SendChatAsync();
        _chatInput.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SendChatAsync();
        };
        _mic.Click += (_, _) =>
        {
            if (_s.Media == null) return;
            _s.Media.AudioEnabled = !_s.Media.AudioEnabled;
            UpdateToggles();
        };
        _cam.Click += (_, _) =>
        {
            if (_s.Media == null) return;
            _s.Media.VideoEnabled = !_s.Media.VideoEnabled;
            UpdateToggles();
        };
        _quality.SelectedIndexChanged += (_, _) =>
        {
            _profile = (VideoProfile)_quality.SelectedIndex;
            if (_s.Media != null) _s.Media.Profile = _profile;
        };
        FormClosing += (_, _) => { _videoTimer.Stop(); _s.Dispose(); };
    }

    private void OnConnected()
    {
        _connected = true;
        var media = _s.Media!;
        media.Profile = _profile;
        media.VideoFrame += (id, jpg) => _latest[id] = jpg;                // keep only the newest frame
        media.Warning += msg => Ui(() => Append("! " + msg));
        media.SettingsChanged += () => Ui(UpdateToggles);
        _name.Text = _s.MyName;                                             // server may have made it unique
        _status.Text = $"Connected as {_s.MyName}";
        _connect.Text = "Disconnect";
        UpdateUi();
    }

    private void OnDisconnected()
    {
        if (!_connected) return;
        _connected = false;
        ClearTiles();
        _rooms.Items.Clear(); _members.Items.Clear();
        _status.Text = "Not connected";
        _connect.Text = "Connect";
        Append("! Disconnected from server");
        UpdateUi();
    }

    private void OnRoomJoined(string room)
    {
        ClearTiles();
        AddTile(0, "Me");
        RefreshMembers();
        Append($"* Entered room {room}");
        UpdateUi();
    }

    private void OnRoomLeft(string why)
    {
        ClearTiles();
        _members.Items.Clear();
        Append($"* {why}");
        UpdateUi();
    }

    // ------------------------------------------------------------------ actions
    private async Task ToggleConnectionAsync()
    {
        if (_connected) { _s.Disconnect(); OnDisconnected(); return; }
        try
        {
            _connect.Enabled = false;
            _status.Text = "Connecting...";
            await _s.ConnectAsync(_host.Text.Trim(), _name.Text.Trim());
        }
        catch (Exception ex)
        {
            _status.Text = "Not connected";
            MessageBox.Show(this, "Could not connect: " + ex.Message, "Conference", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _connect.Enabled = true; }
    }

    private async Task CreateRoomAsync()
    {
        using var dlg = new PromptForm("Create room", ("Room name", false), ("Password (optional)", true));
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Values[0].Length == 0) return;
        await _s.CreateRoomAsync(dlg.Values[0], dlg.Values[1]);
    }

    private RoomInfo? SelectedRoom() => _rooms.SelectedIndex >= 0 && _rooms.SelectedIndex < _roomInfos.Count
        ? _roomInfos[_rooms.SelectedIndex] : null;

    private async Task JoinSelectedAsync()
    {
        var room = SelectedRoom();
        if (room == null || !_connected) return;
        string? password = null;
        if (room.Locked)
        {
            using var dlg = new PromptForm($"Join '{room.Name}'", ("Password", true));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            password = dlg.Values[0];
        }
        await _s.JoinRoomAsync(room.Name, password);
    }

    private async Task DeleteSelectedAsync()
    {
        var room = SelectedRoom();
        if (room == null) return;
        if (MessageBox.Show(this, $"Delete room '{room.Name}'?", "Conference", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await _s.DeleteRoomAsync(room.Name);
    }

    private async Task SendChatAsync()
    {
        var text = _chatInput.Text.Trim();
        if (text.Length == 0 || _s.CurrentRoom == null) return;
        _chatInput.Clear();
        await _s.SendChatAsync(text);
    }

    // ------------------------------------------------------------------ view helpers
    private void ShowRooms(IReadOnlyList<RoomInfo> rooms)
    {
        var selected = SelectedRoom()?.Name;
        _roomInfos = rooms.ToList();
        _rooms.BeginUpdate();
        _rooms.Items.Clear();
        foreach (var r in _roomInfos) _rooms.Items.Add($"{r.Name}  ({r.Members}){(r.Locked ? "  [password]" : "")}");
        var idx = _roomInfos.FindIndex(r => r.Name == selected);
        if (idx >= 0) _rooms.SelectedIndex = idx;
        _rooms.EndUpdate();
    }

    private void RefreshMembers()
    {
        _members.Items.Clear();
        if (_s.CurrentRoom == null) return;
        _members.Items.Add($"{_s.MyName} (you)");
        foreach (var kv in _s.Members.OrderBy(k => k.Value)) _members.Items.Add(kv.Value);
    }

    private void Append(string line)
    {
        _chat.AppendText(line + Environment.NewLine);
        _chat.ScrollToCaret();
    }

    private void UpdateUi()
    {
        bool inRoom = _connected && _s.CurrentRoom != null;
        _create.Enabled = _join.Enabled = _delete.Enabled = _connected;
        _leave.Enabled = _send.Enabled = _chatInput.Enabled = inRoom;
        _mic.Enabled = _cam.Enabled = _connected;
        UpdateToggles();
    }

    private void UpdateToggles()
    {
        _mic.Text = _s.Media is { AudioEnabled: true } ? "Mic: ON" : "Mic: OFF";
        _cam.Text = _s.Media is { VideoEnabled: true } ? "Camera: ON" : "Camera: OFF";
    }

    // ------------------------------------------------------------------ video tiles
    private void AddTile(int id, string name)
    {
        if (_tiles.ContainsKey(id)) return;
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        var label = new Label { Text = name, Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
        var tile = new Panel { Width = 256, Height = 212, Margin = new Padding(6) };
        tile.Controls.Add(pic);
        tile.Controls.Add(label);
        _videoPanel.Controls.Add(tile);
        _tiles[id] = (tile, pic);
    }

    private void RemoveTile(int id)
    {
        if (!_tiles.Remove(id, out var t)) return;
        _latest.TryRemove(id, out _);
        t.Pic.Image?.Dispose();
        _videoPanel.Controls.Remove(t.Tile);
        t.Tile.Dispose();
    }

    private void ClearTiles()
    {
        foreach (var id in _tiles.Keys.ToList()) RemoveTile(id);
        _latest.Clear();
    }

    /// <summary>Runs at ~15 fps on the UI thread: decode only the newest frame of each participant.</summary>
    private void OnVideoTick(object? sender, EventArgs e)
    {
        foreach (var id in _latest.Keys.ToList())
        {
            if (!_latest.TryRemove(id, out var jpg) || !_tiles.TryGetValue(id, out var tile)) continue;
            try
            {
                using var ms = new MemoryStream(jpg);
                using var decoded = Image.FromStream(ms);
                var bmp = new Bitmap(decoded);              // copy, so the stream can be released
                var old = tile.Pic.Image;
                tile.Pic.Image = bmp;
                old?.Dispose();
            }
            catch { /* corrupt frame - skip it */ }
        }
    }
}
