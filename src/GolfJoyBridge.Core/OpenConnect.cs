using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GolfJoyBridge;

// ---------- message classes (property names match the GSPro Open Connect v1 JSON) ----------

public sealed class BallDataMsg
{
    public double Speed { get; set; }
    public double SpinAxis { get; set; }
    public double TotalSpin { get; set; }
    public double BackSpin { get; set; }
    public double SideSpin { get; set; }
    public double HLA { get; set; }
    public double VLA { get; set; }
}

public sealed class ClubDataMsg
{
    public double Speed { get; set; }
    public double AngleOfAttack { get; set; }
    public double FaceToTarget { get; set; }
    public double Lie { get; set; }
    public double Loft { get; set; }
    public double Path { get; set; }
    public double SpeedAtImpact { get; set; }
    public double VerticalFaceImpact { get; set; }
    public double HorizontalFaceImpact { get; set; }
    public double ClosureRate { get; set; }
}

public sealed class ShotOptionsMsg
{
    public bool ContainsBallData { get; set; }
    public bool ContainsClubData { get; set; }
    public bool LaunchMonitorIsReady { get; set; }
    public bool LaunchMonitorBallDetected { get; set; }
    public bool IsHeartBeat { get; set; }
}

public sealed class ShotMessage
{
    public string DeviceID { get; set; } = "";
    public string Units { get; set; } = "Yards";
    public int ShotNumber { get; set; }
    public string APIversion { get; set; } = "1";
    public BallDataMsg BallData { get; set; } = new();
    public ClubDataMsg ClubData { get; set; } = new();
    public ShotOptionsMsg ShotDataOptions { get; set; } = new();
}

// ---------- stream framing ----------

/// <summary>
/// The sim sends JSON objects back to back with no delimiter. This splits a byte/char stream into
/// complete top-level JSON objects, ignoring braces that appear inside strings.
/// </summary>
public sealed class JsonObjectSplitter
{
    private readonly StringBuilder _sb = new();
    private int _depth;
    private bool _inString;
    private bool _escape;

    public List<string> Feed(string chunk)
    {
        var done = new List<string>();
        foreach (char c in chunk)
        {
            if (_depth == 0 && !_inString)
            {
                if (c == '{')
                {
                    _depth = 1;
                    _sb.Clear();
                    _sb.Append(c);
                }
                continue; // ignore anything between objects (whitespace, newlines)
            }

            _sb.Append(c);

            if (_inString)
            {
                if (_escape) _escape = false;
                else if (c == '\\') _escape = true;
                else if (c == '"') _inString = false;
                continue;
            }

            if (c == '"') { _inString = true; continue; }
            if (c == '{') _depth++;
            else if (c == '}')
            {
                _depth--;
                if (_depth == 0)
                {
                    done.Add(_sb.ToString());
                    _sb.Clear();
                }
            }
        }
        return done;
    }
}

// ---------- TCP client ----------

/// <summary>Connects to the sim's Open Connect port, reconnects forever, sends JSON, reports replies.</summary>
public sealed class OpenConnectClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _reconnect;
    private readonly object _startGate = new();
    private readonly object _gate = new();
    private readonly object _sendLock = new();
    private NetworkStream? _stream;
    private bool _connected;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<bool>? ConnectionChanged;
    /// <summary>Raised for every "Player" block from the sim: (handed such as "RH"/"LH", club such as "DR"/"PT").</summary>
    public event Action<string, string>? PlayerInfo;
    /// <summary>Raised for every reply from the sim: (code, message).</summary>
    public event Action<int, string>? Response;

    public OpenConnectClient(string host, int port, TimeSpan reconnectDelay)
    {
        _host = host;
        _port = port;
        _reconnect = reconnectDelay;
    }

    public bool IsConnected { get { lock (_gate) return _connected; } }

    /// <summary>Starts connecting (and reconnecting). Safe to call again after Stop().</summary>
    public void Start()
    {
        lock (_startGate)
        {
            if (_loop != null && !_loop.IsCompleted) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
    }

    /// <summary>Closes the connection to the sim and stops reconnecting.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_startGate)
        {
            cts = _cts; loop = _loop;
            _cts = null; _loop = null;
        }
        if (cts == null) return;
        cts.Cancel();
        lock (_gate) { try { _stream?.Close(); } catch { } }
        try { loop?.Wait(2000); } catch { }
        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool loggedWaiting = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                var stream = client.GetStream();
                lock (_gate) { _stream = stream; _connected = true; }
                loggedWaiting = false;
                Log.Info($"Connected to sim at {_host}:{_port}");
                RaiseConnection(true);
                await ReceiveLoop(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                if (!loggedWaiting)
                {
                    Log.Info($"Sim not reachable at {_host}:{_port} ({ex.GetType().Name}). Will keep retrying.");
                    loggedWaiting = true;
                }
            }
            finally
            {
                bool wasConnected;
                lock (_gate) { _stream = null; wasConnected = _connected; _connected = false; }
                if (wasConnected)
                {
                    Log.Info("Sim connection closed");
                    RaiseConnection(false);
                }
            }

            try { await Task.Delay(_reconnect, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoop(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var splitter = new JsonObjectSplitter();
        var chars = new char[8192];
        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n == 0) return; // remote closed
            int count = decoder.GetChars(buffer, 0, n, chars, 0);
            foreach (var json in splitter.Feed(new string(chars, 0, count)))
                HandleMessage(json);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int code = root.TryGetProperty("Code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            string message = root.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            Response?.Invoke(code, message);

            if (root.TryGetProperty("Player", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                string handed = p.TryGetProperty("Handed", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
                string club = p.TryGetProperty("Club", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString() ?? "" : "";
                PlayerInfo?.Invoke(handed, club);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Could not read sim message ({ex.GetType().Name}): {json}");
        }
    }

    private void RaiseConnection(bool connected)
    {
        try { ConnectionChanged?.Invoke(connected); } catch (Exception ex) { Log.Info("ConnectionChanged handler failed: " + ex.Message); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string Serialize(ShotMessage message) => JsonSerializer.Serialize(message, JsonOptions);

    /// <summary>Sends one message. Returns false when not connected or the write failed.</summary>
    public bool Send(ShotMessage message)
    {
        NetworkStream? stream;
        lock (_gate) stream = _stream;
        if (stream == null) return false;

        byte[] bytes = Encoding.UTF8.GetBytes(Serialize(message));
        try
        {
            lock (_sendLock) stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Send to sim failed ({ex.GetType().Name}); connection will be re-established.");
            try { stream.Close(); } catch { }
            return false;
        }
    }

    public void Dispose() => Stop();
}
