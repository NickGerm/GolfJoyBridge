using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GolfJoyBridge;

// Tiny self-contained test runner (no external packages needed).
int failures = 0, checks = 0;
void Check(bool ok, string name)
{
    checks++;
    if (!ok) { failures++; Console.WriteLine("  FAIL: " + name); }
}
void Near(double actual, double expected, double tol, string name) =>
    Check(Math.Abs(actual - expected) <= tol, $"{name} (got {actual}, expected {expected})");
void Section(string n) => Console.WriteLine("- " + n);

// ---------------------------------------------------------------- INI / settings
Section("settings");
{
    string dir = Path.Combine(Path.GetTempPath(), "soc_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string ini = Path.Combine(dir, "t.ini");
    var warnings = new List<string>();
    var s = Settings.Load(ini, warnings.Add);
    Check(File.Exists(ini), "default ini is created");
    Check(warnings.Count == 0, "default ini loads with no warnings: " + string.Join("|", warnings));
    var d = new Settings();
    Check(s.Port == d.Port && s.UnitIp == "192.168.147.147" && s.CameraMode == CameraMode.Loop, "defaults match class defaults");
    Check(s.PutterClub == 45 && s.DriverClub == 1 && s.PopupRepeats == 3 && !s.PopupTakeFocus, "more defaults");
    Check(File.ReadAllText(ini).Contains("\r\n"), "ini written with Windows line endings");

    File.WriteAllText(ini, "[Sim]\nPort = 922 ; alt port\nHost=10.0.0.5\nClubWaitMs = abc\nPutterClub=99999\nAutoSwitchHand = no\n[Camera]\nMode = Popup\nPopupRepeats = 5\nGamma = 0.8\nPopupDelaySeconds=1.5\n; comment\n# other comment\n[Unit]\nDefaultHand = LEFT\n");
    warnings.Clear();
    s = Settings.Load(ini, warnings.Add);
    Check(s.Port == 922 && s.Host == "10.0.0.5", "port/host parsed with inline comment");
    Check(s.ClubWaitMs == 1500, "bad number falls back to default");
    Check(s.PutterClub == 45, "out-of-range falls back to default");
    Check(warnings.Count == 2, "two warnings for the two bad values (got " + warnings.Count + ")");
    Check(!s.AutoSwitchHand, "bool 'no' parsed");
    Check(s.CameraMode == CameraMode.Popup && s.PopupRepeats == 5, "camera mode and repeats parsed");
    Near(s.Gamma, 0.8, 1e-9, "gamma parsed"); Near(s.PopupDelaySeconds, 1.5, 1e-9, "decimal parsed with invariant culture");
    Check(s.DefaultHand == "left", "choice is case-insensitive");
    Check(Settings.ResolvePath("/base", "x/y.dll").EndsWith("y.dll") && Settings.ResolvePath("/base", "/abs/z.dll") == "/abs/z.dll", "path resolution");

    // Save() round-trip: change a bunch of values, save, reload, and the new file should still have every comment.
    var original = File.ReadAllText(ini);
    int commentLines = original.Split('\n').Count(l => l.TrimStart().StartsWith(";"));
    s.Mode = GolfJoyBridge.ConnectionMode.Wireless;
    s.WirelessUnitIp = "192.168.1.50";
    s.CameraMode = CameraMode.Popup;
    s.PopupRepeats = 7;
    s.PopupX = 120;
    s.Gamma = 0.75;
    s.DeviceId = "My Bridge";
    s.Save(ini);
    var saved = File.ReadAllText(ini);
    int commentLinesAfter = saved.Split('\n').Count(l => l.TrimStart().StartsWith(";"));
    Check(commentLinesAfter == commentLines, $"Save() keeps every comment line ({commentLinesAfter} vs {commentLines})");
    warnings.Clear();
    var reloaded = Settings.Load(ini, warnings.Add);
    Check(warnings.Count == 0, "reloaded file has no warnings: " + string.Join("|", warnings));
    Check(reloaded.Mode == GolfJoyBridge.ConnectionMode.Wireless && reloaded.WirelessUnitIp == "192.168.1.50", "wireless settings round-trip");
    Check(reloaded.CameraMode == CameraMode.Popup && reloaded.PopupRepeats == 7 && reloaded.PopupX == 120, "camera settings round-trip");
    Near(reloaded.Gamma, 0.75, 1e-9, "gamma round-trip");
    Check(reloaded.DeviceId == "My Bridge", "string setting round-trip");
    Check(reloaded.Port == 922 && reloaded.ClubWaitMs == 1500, "untouched settings from the earlier edit survive Save()");

    // saving back to wired should round-trip cleanly too
    reloaded.Mode = GolfJoyBridge.ConnectionMode.Wired;
    reloaded.Save(ini);
    var backToWired = Settings.Load(ini, w => { });
    Check(backToWired.Mode == GolfJoyBridge.ConnectionMode.Wired, "switching back to wired round-trips");

    Directory.Delete(dir, true);
}

// ---------------------------------------------------------------- JSON framing
Section("json splitter");
{
    var sp = new JsonObjectSplitter();
    var got = new List<string>();
    string stream = "{\"Code\":200,\"Message\":\"a } brace { in text\"}{\"Code\":201,\"Player\":{\"Handed\":\"LH\",\"Club\":\"PT\"}}\r\n  {\"x\":\"q\\\"uote}\"}";
    foreach (var ch in Chunks(stream, 7)) got.AddRange(sp.Feed(ch));
    Check(got.Count == 3, "three objects out of a chunked stream (got " + got.Count + ")");
    Check(got.Count == 3 && JsonDocument.Parse(got[1]).RootElement.GetProperty("Player").GetProperty("Club").GetString() == "PT", "nested object intact");
    Check(got.Count == 3 && got[0].Contains("a } brace { in text") && got[2].Contains("q\\\"uote}"), "braces inside strings ignored");
}
static IEnumerable<string> Chunks(string s, int n) { for (int i = 0; i < s.Length; i += n) yield return s.Substring(i, Math.Min(n, s.Length - i)); }

// ---------------------------------------------------------------- shot mapping with real logged values
Section("shot mapping (your logged shots)");
{
    var s = new Settings();
    // run.txt shot 1, full swing
    var m = ShotMapper.Map(s, new SpicaBall(44.3927f, 21.4812f, -2.3952f, 6251.7925f, 787.3911f), new SpicaClub(34.7053f, -3.8705f, -9999f, -5.318f), 1).Message;
    Near(m.BallData.Speed, 99.3, 0.06, "ball speed mph");
    Near(m.BallData.VLA, 21.48, 0.01, "VLA"); Near(m.BallData.HLA, -2.40, 0.01, "HLA");
    Near(m.BallData.BackSpin, 6252, 0.5, "backspin"); Near(m.BallData.SideSpin, 787, 0.5, "sidespin");
    Near(m.BallData.TotalSpin, 6301, 1, "total spin"); Near(m.BallData.SpinAxis, 7.2, 0.05, "spin axis");
    Check(m.ShotDataOptions.ContainsClubData, "club data flagged");
    Near(m.ClubData.Speed, 77.6, 0.06, "club speed mph"); Near(m.ClubData.Path, -3.87, 0.01, "club path");
    Near(m.ClubData.AngleOfAttack, -5.32, 0.01, "attack angle"); Near(m.ClubData.FaceToTarget, 0, 1e-9, "face not measured -> 0");
    Check(m.DeviceID == s.DeviceId && m.APIversion == "1" && m.Units == "Yards" && m.ShotNumber == 1, "root fields");

    // shot 2
    var m2 = ShotMapper.Map(s, new SpicaBall(41.8062f, 14.284f, 1.8542f, 6248.9985f, 1863.9518f), new SpicaClub(36.2331f, -3.9703f, -9999f, -7.0813f), 2).Message;
    Near(m2.BallData.Speed, 93.5, 0.06, "shot 2 speed"); Near(m2.BallData.TotalSpin, 6521, 1, "shot 2 total spin"); Near(m2.BallData.SpinAxis, 16.6, 0.05, "shot 2 axis");
    Near(m2.ClubData.Speed, 81.1, 0.06, "shot 2 club speed");

    // putt #1 and #2: spin -99999, club speed -9959.5
    var p = ShotMapper.Map(s, new SpicaBall(1.8613803f, 0f, 0.18610707f, -99999f, -99999f), new SpicaClub(-9959.4785f, -9999f, -9999f, -9999f), 3);
    Check(p.IsPutt, "putt detected from missing spin");
    Near(p.Message.BallData.Speed, 4.16, 0.01, "putt speed mph"); Near(p.Message.BallData.HLA, 0.19, 0.01, "putt direction");
    Check(p.Message.BallData.VLA == 0 && p.Message.BallData.BackSpin == 0 && p.Message.BallData.SideSpin == 0 && p.Message.BallData.TotalSpin == 0 && p.Message.BallData.SpinAxis == 0, "putt spin/launch zeroed");
    Check(!p.Message.ShotDataOptions.ContainsClubData, "putt has no club data");
    Check(p.Summary.StartsWith("PUTT #3"), "putt summary");
    var p2 = ShotMapper.Map(s, new SpicaBall(4.3154755f, 0f, -1.3507525f, -99999f, -99999f), null, 4);
    Near(p2.Message.BallData.Speed, 9.65, 0.01, "putt 2 speed"); Near(p2.Message.BallData.HLA, -1.35, 0.01, "putt 2 direction");

    // options
    var inv = new Settings { InvertDirection = true, InvertSideSpin = true, SendClubData = false };
    var mi = ShotMapper.Map(inv, new SpicaBall(44.3927f, 21.4812f, -2.3952f, 6251.7925f, 787.3911f), new SpicaClub(34.7053f, -3.8705f, 1f, -5.318f), 5).Message;
    Near(mi.BallData.HLA, 2.40, 0.01, "HLA inverted"); Near(mi.BallData.SideSpin, -787, 0.5, "sidespin inverted"); Near(mi.BallData.SpinAxis, -7.2, 0.05, "axis inverted");
    Check(!mi.ShotDataOptions.ContainsClubData, "SendClubData=false respected");

    // JSON shape
    var json = JsonDocument.Parse(OpenConnectClient.Serialize(m)).RootElement;
    Check(json.GetProperty("BallData").GetProperty("Speed").GetDouble() > 99 && json.GetProperty("ShotDataOptions").GetProperty("ContainsBallData").GetBoolean(), "serialised JSON has GSPro field names");
    Check(json.GetProperty("APIversion").GetString() == "1" && json.TryGetProperty("ClubData", out _) && json.GetProperty("ClubData").TryGetProperty("SpeedAtImpact", out _), "APIversion and ClubData present");
}

// ---------------------------------------------------------------- camera frames
Section("camera frames");
{
    var data = new byte[4 * 3 * 2];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 10);
    var fs = FrameSet.FromRaw(new RawImage(4, 3, 2, 1, data), 0.5);
    Check(fs != null && fs.Count == 2 && fs.Width == 4 && fs.Height == 3, "frames split");
    Check(fs != null && fs.Frames[0][0] == 0 && fs.Frames[0][1] > data[1] && fs.Frames[1][0] > data[12], "gamma < 1 brightens");
    Check(FrameSet.FromRaw(new RawImage(4, 3, 2, 2, new byte[48]), 1.0) == null, "unsupported pixel size -> null");
    Check(FrameSet.FromRaw(new RawImage(4, 3, 2, 1, new byte[5]), 1.0) == null, "short buffer -> null");
    var same = FrameSet.FromRaw(new RawImage(4, 3, 1, 1, new byte[12].Select((_, i) => (byte)(i * 20)).ToArray()), 1.0);
    Check(same != null && same.Frames[0].SequenceEqual(new byte[12].Select((_, i) => (byte)(i * 20))), "gamma 1.0 leaves pixels unchanged");
}

// ---------------------------------------------------------------- shot assembler timing
Section("shot assembler");
{
    var s = new Settings { ClubWaitMs = 300 };
    using var asm = new ShotAssembler(s);
    var shots = new List<MappedShot>();
    asm.ShotReady += shots.Add;
    var ball = new SpicaBall(44.4f, 21.5f, -2.4f, 6252f, 787f);
    asm.OnBall(ball); asm.OnClub(new SpicaClub(34.7f, -3.9f, -9999f, -5.3f));
    Check(shots.Count == 1 && shots[0].Message.ShotDataOptions.ContainsClubData, "ball + club -> one shot immediately");
    asm.OnBall(ball);
    Thread.Sleep(600);
    Check(shots.Count == 2 && !shots[1].Message.ShotDataOptions.ContainsClubData, "ball only -> sent after ClubWaitMs");
    asm.OnClub(new SpicaClub(30f, 0f, 0f, 0f)); Thread.Sleep(50);
    Check(shots.Count == 2, "stray club data ignored");
    asm.OnBall(ball); asm.OnBall(ball); Thread.Sleep(600);
    Check(shots.Count == 4 && shots[2].Message.ShotNumber == 3 && shots[3].Message.ShotNumber == 4, "back-to-back shots both sent, numbered in order");
}

// ---------------------------------------------------------------- open connect client vs a fake sim
Section("open connect client");
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var players = new List<(string, string)>();
    var codes = new List<int>();
    bool connected = false; int connects = 0;
    using var client = new OpenConnectClient("127.0.0.1", port, TimeSpan.FromMilliseconds(200));
    client.PlayerInfo += (h, c) => players.Add((h, c));
    client.Response += (code, _) => codes.Add(code);
    client.ConnectionChanged += c => { connected = c; if (c) connects++; };
    client.Start();

    var tcp = listener.AcceptTcpClient();
    Wait(() => connected, 3000);
    Check(connected && client.IsConnected, "client connects");

    var smsg = ShotMapper.Map(new Settings(), new SpicaBall(44.4f, 21.5f, -2.4f, 6252f, 787f), null, 7).Message;
    Check(client.Send(smsg), "send returns true");
    var ns = tcp.GetStream();
    ns.ReadTimeout = 3000;
    var buf = new byte[4096];
    int n = ns.Read(buf, 0, buf.Length);
    var got = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, n)).RootElement;
    Check(got.GetProperty("ShotNumber").GetInt32() == 7 && got.GetProperty("BallData").GetProperty("Speed").GetDouble() > 99, "server receives the shot JSON");

    // two replies glued together, split across writes
    byte[] reply = Encoding.UTF8.GetBytes("{\"Code\":200,\"Message\":\"ok\"}{\"Code\":201,\"Message\":\"GSPro Player Information\",\"Player\":{\"Handed\":\"LH\",\"Club\":\"PT\"}}");
    ns.Write(reply, 0, 20); ns.Flush(); Thread.Sleep(80); ns.Write(reply, 20, reply.Length - 20); ns.Flush();
    Wait(() => players.Count == 1, 3000);
    Check(codes.SequenceEqual(new[] { 200, 201 }), "both replies parsed");
    Check(players.Count == 1 && players[0] == ("LH", "PT"), "player info delivered");

    tcp.Close();
    Wait(() => !connected, 3000);
    Check(!connected && !client.Send(smsg), "disconnect noticed, send fails while down");
    var tcp2 = listener.AcceptTcpClient();
    Wait(() => connected, 3000);
    Check(connected && connects == 2, "client reconnects automatically");
    tcp2.Close();
    listener.Stop();
}

// ---------------------------------------------------------------- whole service with fake unit + fake sim
Section("connector service");
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var s = new Settings { Port = port, ReconnectSeconds = 0.5, HeartbeatSeconds = 1, ClubWaitMs = 400, DefaultHand = "right" };
    var dev = new FakeDevice();
    using var svc = new ConnectorService(s, dev) { ReadyPollMs = 20 };
    var frames = new List<FrameSet>();
    svc.CameraReady += frames.Add;
    Check(svc.Start(out var err), "service starts: " + err);

    var tcp = Accept(listener, 5000);
    Check(tcp != null, "sim connection opens once the unit is ready");
    if (tcp == null) { Console.WriteLine("cannot continue service test"); return 1; }
    var ns = tcp.GetStream();
    var splitter = new JsonObjectSplitter();
    var msgs = new List<JsonElement>();
    var lockObj = new object();
    var reader = Task.Run(() =>
    {
        var b = new byte[8192];
        try
        {
            while (true)
            {
                int n = ns.Read(b, 0, b.Length);
                if (n <= 0) break;
                foreach (var j in splitter.Feed(Encoding.UTF8.GetString(b, 0, n)))
                    lock (lockObj) msgs.Add(JsonDocument.Parse(j).RootElement.Clone());
            }
        }
        catch { }
    });
    List<JsonElement> Snapshot() { lock (lockObj) return msgs.ToList(); }

    Wait(() => svc.UnitReady, 3000);
    Check(svc.UnitReady, "unit becomes ready once it reports a hand");
    Wait(() => dev.HandCalls.Count >= 1, 3000);
    Check(dev.HandCalls.SequenceEqual(new[] { true }), "default hand (right) applied after ready");
    Wait(() => Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("LaunchMonitorIsReady").GetBoolean()), 3000);
    Check(Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("LaunchMonitorIsReady").GetBoolean()), "sim is told the unit is ready");
    Wait(() => Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("IsHeartBeat").GetBoolean()), 4000);
    Check(Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("IsHeartBeat").GetBoolean()), "heartbeats are sent");

    // sim says: left-handed, putter
    byte[] player = Encoding.UTF8.GetBytes("{\"Code\":201,\"Message\":\"GSPro Player Information\",\"Player\":{\"Handed\":\"LH\",\"Club\":\"PT\"}}");
    ns.Write(player, 0, player.Length); ns.Flush();
    Wait(() => dev.ClubCalls.Count == 1 && dev.HandCalls.Count == 2, 3000);
    Check(dev.ClubCalls.SequenceEqual(new[] { 45 }) && dev.HandCalls.SequenceEqual(new[] { true, false }), "LH + PT -> left hand, putter club 45");
    ns.Write(player, 0, player.Length); ns.Flush(); Thread.Sleep(150);
    Check(dev.ClubCalls.Count == 1 && dev.HandCalls.Count == 2, "repeated identical player info does not re-send commands");
    byte[] driver = Encoding.UTF8.GetBytes("{\"Code\":201,\"Player\":{\"Handed\":\"RH\",\"Club\":\"DR\"}}");
    ns.Write(driver, 0, driver.Length); ns.Flush();
    Wait(() => dev.ClubCalls.Count == 2, 3000);
    byte[] iron = Encoding.UTF8.GetBytes("{\"Code\":201,\"Player\":{\"Handed\":\"RH\",\"Club\":\"I7\"}}");
    ns.Write(iron, 0, iron.Length); ns.Flush();
    Wait(() => dev.ClubCalls.Count == 3, 3000);
    Check(dev.ClubCalls.SequenceEqual(new[] { 45, 1, 10 }), "DR -> driver 1, I7 -> iron 10");

    // ball placed, then a shot with club data, then camera
    dev.RaiseBallState(1);
    Wait(() => Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("LaunchMonitorBallDetected").GetBoolean()), 3000);
    Check(Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("LaunchMonitorBallDetected").GetBoolean()), "ball detected is reported");
    int before = Snapshot().Count;
    dev.RaiseBall(new SpicaBall(44.3927f, 21.4812f, -2.3952f, 6251.7925f, 787.3911f));
    dev.RaiseClub(new SpicaClub(34.7053f, -3.8705f, -9999f, -5.318f));
    Wait(() => Snapshot().Any(m => m.GetProperty("ShotDataOptions").GetProperty("ContainsBallData").GetBoolean()), 3000);
    var shotMsg = Snapshot().FirstOrDefault(m => m.GetProperty("ShotDataOptions").GetProperty("ContainsBallData").GetBoolean());
    Check(shotMsg.ValueKind == JsonValueKind.Object && Math.Abs(shotMsg.GetProperty("BallData").GetProperty("Speed").GetDouble() - 99.3) < 0.1 && shotMsg.GetProperty("ShotDataOptions").GetProperty("ContainsClubData").GetBoolean(), "full swing reaches the sim with club data");

    dev.RaiseImage(new RawImage(8, 4, 3, 1, new byte[8 * 4 * 3]));
    Wait(() => frames.Count == 1, 3000);
    Check(frames.Count == 1 && frames[0].Count == 3, "camera frames delivered to the UI layer");
    tcp.Close();
    listener.Stop();
}

// ---------------------------------------------------------------- sim connection follows the unit
Section("sim connection follows the unit");
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var s = new Settings { Port = port, ReconnectSeconds = 0.3, HeartbeatSeconds = 1, DefaultHand = "right" };
    var dev = new FakeDevice { AutoConnect = false };
    using var svc = new ConnectorService(s, dev) { ReadyPollMs = 20, NoUnitHintMs = 100 };
    Check(svc.Start(out _), "starts without a unit present");

    Thread.Sleep(900);
    Check(Accept(listener, 10) == null, "no unit -> the sim is NOT connected");
    Check(svc.State == UnitState.Waiting && !svc.SimConnected, "state is Waiting, sim not connected");
    dev.RaiseConnection(false);
    Check(svc.State == UnitState.Waiting, "a 'lost' report before the unit was ever seen is ignored");

    dev.RaiseConnection(true);
    var tcp = Accept(listener, 3000);
    Check(tcp != null, "unit connected -> sim connection opens");
    Wait(() => svc.State == UnitState.Ready && svc.SimConnected, 3000);
    Check(svc.State == UnitState.Ready && svc.SimConnected, "state Ready and sim connected");
    Wait(() => dev.HandCalls.Count >= 1, 3000);
    Check(dev.HandCalls.Count == 1, "hand applied once");

    dev.RaiseConnection(false);
    Wait(() => svc.State == UnitState.Lost && !svc.SimConnected, 3000);
    Check(svc.State == UnitState.Lost && !svc.SimConnected, "unit lost -> state Lost, sim disconnected");
    bool closed = false;
    if (tcp != null)
    {
        var ns = tcp.GetStream();
        ns.ReadTimeout = 3000;
        try { var b = new byte[4096]; while (true) { if (ns.Read(b, 0, b.Length) == 0) { closed = true; break; } } }
        catch { closed = true; }
    }
    Check(closed, "the sim's socket was really closed");
    Check(Accept(listener, 800) == null, "no reconnecting to the sim while the unit is lost");
    Check(svc.StatusText.Contains("LOST"), "status text says the unit is lost");

    int before = dev.HandCalls.Count;
    dev.RaiseConnection(true);
    var tcp2 = Accept(listener, 3000);
    Check(tcp2 != null, "unit back -> sim connection opens again");
    Wait(() => dev.HandCalls.Count > before, 3000);
    Check(dev.HandCalls.Count > before, "hand sent to the unit again after it came back");
    Check(svc.State == UnitState.Ready, "Ready again");
    listener.Stop();
}

// ---------------------------------------------------------------- wireless relay
Section("wireless relay");
{
    // The relay always listens on the real 5288/5277 (that is what GRS.dll expects), so this test's
    // "fake unit" binds those same port numbers on a different loopback address (127.0.0.2) and the
    // relay is told to forward there.
    bool canBind = true;
    TcpListener? probe5288 = null;
    try { probe5288 = new TcpListener(IPAddress.Loopback, 5288); probe5288.Start(); probe5288.Stop(); }
    catch { canBind = false; }

    if (!canBind)
    {
        Console.WriteLine("  (skipped: port 5288 is already in use on this machine)");
    }
    else
    {
        var fakeUnit5288 = new TcpListener(IPAddress.Parse("127.0.0.2"), 5288);
        var fakeUnit5277 = new TcpListener(IPAddress.Parse("127.0.0.2"), 5277);
        fakeUnit5288.Start();
        fakeUnit5277.Start();

        using var relay = new WirelessRelay("127.0.0.2");
        relay.Start();
        await Task.Delay(200);

        // UDP probe -> relay should answer with the known-good captured bytes, without the fake unit involved at all.
        using (var udp = new UdpClient(0))
        {
            byte[] probe = new byte[] { 0xAA, 0x55, 0xCE, 18, 0 }.Concat(Encoding.ASCII.GetBytes("Get firmware ver\r\n")).ToArray();
            await udp.SendAsync(probe, probe.Length, "127.0.0.1", 8813);
            udp.Client.ReceiveTimeout = 3000;
            var remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] reply = udp.Receive(ref remote);
            Check(reply.Length > 5 && reply[0] == 0xAA && reply[1] == 0x55 &&
                  Encoding.ASCII.GetString(reply, 5, reply.Length - 5).Contains("Firmware ver"),
                  "relay answers the UDP probe with a firmware-looking reply");
        }

        // TCP: data sent into the relay's 5288 should arrive, byte for byte, at the fake unit, and back.
        var acceptTask = fakeUnit5288.AcceptTcpClientAsync();
        using var toRelay = new TcpClient();
        await toRelay.ConnectAsync("127.0.0.1", 5288);
        using var atUnit = await acceptTask.WaitAsync(TimeSpan.FromSeconds(3));

        byte[] outbound = Encoding.UTF8.GetBytes("hello unit");
        await toRelay.GetStream().WriteAsync(outbound);
        var buf = new byte[64];
        int n = await atUnit.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(Encoding.UTF8.GetString(buf, 0, n) == "hello unit", "bytes sent into the relay reach the fake unit unmodified");

        byte[] inbound = Encoding.UTF8.GetBytes("hello DLL");
        await atUnit.GetStream().WriteAsync(inbound);
        int n2 = await toRelay.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(Encoding.UTF8.GetString(buf, 0, n2) == "hello DLL", "bytes from the fake unit reach the DLL side unmodified");

        try { fakeUnit5288.Stop(); fakeUnit5277.Stop(); } catch { }
    }
}

// ---------------------------------------------------------------- unit never becomes ready
Section("service with an unreachable unit");
{
    var s = new Settings { Port = 1 }; // nothing listens; just checking it does not blow up
    var dev = new FakeDevice { StartError = "boom" };
    using var svc = new ConnectorService(s, dev);
    Check(!svc.Start(out var err) && err == "boom", "start failure is reported with the message");
}

Console.WriteLine();
Console.WriteLine($"{checks - failures}/{checks} checks passed" + (failures == 0 ? "  -> ALL GOOD" : $"  -> {failures} FAILED"));
return failures == 0 ? 0 : 1;

static TcpClient? Accept(TcpListener l, int ms)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        if (l.Pending()) return l.AcceptTcpClient();
        Thread.Sleep(10);
    }
    return null;
}

static void Wait(Func<bool> cond, int ms)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(10);
}

sealed class FakeDevice : ILaunchDevice
{
    public event Action<SpicaBall>? Ball;
    public event Action<SpicaClub>? Club;
    public event Action<int>? BallState;
    public event Action<RawImage>? Image;
    public event Action<bool>? Connection;
    public bool AutoConnect = true;
    public string? StartError;
    public List<bool> HandCalls = new();
    public List<int> ClubCalls = new();
    private int _hand = -1;
    private int _polls;

    public bool Start(out string error)
    {
        error = StartError ?? "";
        if (StartError == null && AutoConnect)
            Task.Run(async () => { await Task.Delay(30); Connection?.Invoke(true); });
        return StartError == null;
    }
    public void RaiseConnection(bool c) => Connection?.Invoke(c);
    public void Stop() { }
    public int GetHand() { if (++_polls >= 3) _hand = 1; return _hand; }   // reports -1 for a moment like the real unit
    public void SetHand(bool right) { HandCalls.Add(right); _hand = right ? 1 : 0; }
    public void SetClub(int n) => ClubCalls.Add(n);
    public string Describe() => "fake unit";
    public void Dispose() { }
    public void RaiseBall(SpicaBall b) => Ball?.Invoke(b);
    public void RaiseClub(SpicaClub c) => Club?.Invoke(c);
    public void RaiseBallState(int s) => BallState?.Invoke(s);
    public void RaiseImage(RawImage i) => Image?.Invoke(i);
}
