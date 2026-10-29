namespace GolfJoyBridge;

public enum UnitState
{
    /// <summary>No heartbeat from the unit yet (not plugged in, powered off, wrong IP...).</summary>
    Waiting,
    /// <summary>Heartbeat received; waiting for the unit to finish its start-up handshake.</summary>
    Connecting,
    /// <summary>Unit is fully connected. The connection to the sim is open only in this state.</summary>
    Ready,
    /// <summary>The unit was connected but its heartbeat stopped.</summary>
    Lost,
}

/// <summary>
/// The whole program minus the windows: launch monitor in, Open Connect out, camera frames to whoever listens.
/// The connection to the sim is opened only while the unit is connected, so the sim never shows
/// "connected" for a launch monitor that is not there.
/// </summary>
public sealed class ConnectorService : IDisposable
{
    private readonly Settings _s;
    private readonly ILaunchDevice _device;
    private readonly ShotAssembler _assembler;
    private readonly OpenConnectClient _client;
    private readonly WirelessRelay? _relay;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Timer? _heartbeat;

    private UnitState _state = UnitState.Waiting;
    private int _generation;            // bumped on every connect/lost event so stale waiters stop
    private bool _simConnected;
    private bool _ballDetected;
    private bool? _desiredRight;
    private int? _desiredClub;
    private bool? _appliedRight;
    private int? _appliedClub;

    /// <summary>How often to check whether the unit has finished its handshake. Tests lower this.</summary>
    public int ReadyPollMs { get; set; } = 500;

    /// <summary>How long to wait for a first heartbeat before logging a hint. Tests lower this.</summary>
    public int NoUnitHintMs { get; set; } = 15000;

    public event Action<FrameSet>? CameraReady;
    public event Action<string>? StatusChanged;
    public event Action<MappedShot>? ShotSent;

    public ConnectorService(Settings settings, ILaunchDevice device)
    {
        _s = settings;
        _device = device;
        _assembler = new ShotAssembler(settings);
        _client = new OpenConnectClient(settings.Host, settings.Port, TimeSpan.FromSeconds(settings.ReconnectSeconds));
        if (settings.Mode == ConnectionMode.Wireless && !string.IsNullOrWhiteSpace(settings.WirelessUnitIp))
            _relay = new WirelessRelay(settings.WirelessUnitIp);

        _device.Ball += _assembler.OnBall;
        _device.Club += _assembler.OnClub;
        _device.BallState += OnBallState;
        _device.Image += OnImage;
        _device.Connection += OnUnitConnection;

        _assembler.ShotReady += OnShotReady;
        _client.ConnectionChanged += OnSimConnection;
        _client.PlayerInfo += OnPlayerInfo;
        _client.Response += (code, msg) => { if (code >= 500) Log.Info($"Sim replied {code}: {msg}"); };
    }

    public UnitState State { get { lock (_gate) return _state; } }
    public bool UnitReady => State == UnitState.Ready;
    public bool SimConnected { get { lock (_gate) return _simConnected; } }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                string unit = _state switch
                {
                    UnitState.Waiting => "not found yet...",
                    UnitState.Connecting => "connecting...",
                    UnitState.Ready => "ready",
                    _ => "LOST (no heartbeat)",
                };
                return $"Unit: {unit} | Sim: {(_simConnected ? "connected" : "not connected")}";
            }
        }
    }

    /// <summary>Starts the unit. The sim connection is opened later, once the unit reports in.</summary>
    public bool Start(out string error)
    {
        if (_s.Mode == ConnectionMode.Wireless && _relay == null)
        {
            error = "ConnectionMode is wireless but WirelessUnitIp is not set. Give the unit a reserved IP on your router and put it in Settings.";
            return false;
        }
        _relay?.Start();
        if (!_device.Start(out error)) return false;

        _heartbeat = new Timer(_ => SendStatus(heartbeat: true), null,
            TimeSpan.FromSeconds(_s.HeartbeatSeconds), TimeSpan.FromSeconds(_s.HeartbeatSeconds));
        Task.Run(HintIfNoUnitAsync);
        RaiseStatus();
        return true;
    }

    private async Task HintIfNoUnitAsync()
    {
        try
        {
            await Task.Delay(NoUnitHintMs, _cts.Token).ConfigureAwait(false);
            if (State == UnitState.Waiting)
                Log.Info("No heartbeat from the unit yet. Check the cable, that the unit is on, the UnitIp in the ini file, and that the PC's network adapter is on the same subnet. The sim stays disconnected until the unit shows up.");
        }
        catch (OperationCanceledException) { }
    }

    // ---------- unit connection ----------

    private void OnUnitConnection(bool connected)
    {
        int gen;
        lock (_gate)
        {
            if (!connected && _state == UnitState.Waiting) return; // "lost" before it was ever seen: nothing to do
            if (connected && (_state == UnitState.Ready || _state == UnitState.Connecting)) return; // duplicate notification
            gen = ++_generation;
            if (connected)
            {
                _state = UnitState.Connecting;
            }
            else
            {
                _state = UnitState.Lost;
                _ballDetected = false;
                // The unit may have rebooted: send hand/club again when it comes back.
                _appliedRight = null;
                _appliedClub = null;
            }
        }

        if (connected)
        {
            Log.Info("Unit connected (heartbeat received)");
            RaiseStatus();
            Task.Run(() => WaitForHandshakeAsync(gen));
        }
        else
        {
            Log.Info("Unit lost: no heartbeat. Disconnecting from the sim until it comes back.");
            RaiseStatus();
            Task.Run(() => _client.Stop()); // off the unit's callback thread
        }
    }

    private async Task WaitForHandshakeAsync(int gen)
    {
        try
        {
            // The unit counts as fully connected once it reports a hand (0 or 1); before its handshake finishes it reports -1.
            for (int i = 0; !_cts.IsCancellationRequested; i++)
            {
                lock (_gate) { if (gen != _generation) return; }

                int hand = _device.GetHand();
                if (hand == 0 || hand == 1)
                {
                    lock (_gate)
                    {
                        if (gen != _generation) return;
                        _state = UnitState.Ready;
                        if (_desiredRight == null)
                        {
                            if (_s.DefaultHand == "right") _desiredRight = true;
                            else if (_s.DefaultHand == "left") _desiredRight = false;
                            else _appliedRight = hand == 1;
                        }
                    }
                    Log.Info("Unit ready: " + _device.Describe() + $" | hand {(hand == 1 ? "right" : "left")}");
                    ApplyDesired();
                    _client.Start();      // only now does the sim see us as connected
                    RaiseStatus();
                    return;
                }

                if (i == 60)
                    Log.Info("The unit is sending heartbeats but has not finished its handshake yet. Still waiting.");
                await Task.Delay(ReadyPollMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Info("Waiting for the unit failed: " + ex.Message); }
    }

    // ---------- unit -> sim ----------

    private void OnBallState(int state)
    {
        bool changed;
        lock (_gate)
        {
            bool detected = state == 1;
            changed = detected != _ballDetected;
            _ballDetected = detected;
        }
        Log.Info($"Ball state {state}");
        if (changed) SendStatus(heartbeat: false);
    }

    private void OnShotReady(MappedShot shot)
    {
        Log.Info(shot.Summary);
        bool sent = _client.Send(shot.Message);
        if (!sent) Log.Info("(shot not sent: no connection to the sim)");
        try { ShotSent?.Invoke(shot); } catch (Exception ex) { Log.Info("ShotSent handler failed: " + ex.Message); }
    }

    private void OnImage(RawImage raw)
    {
        // The unit's callback thread must not be held up, so convert off-thread.
        Task.Run(() =>
        {
            try
            {
                var frames = FrameSet.FromRaw(raw, _s.Gamma);
                if (frames != null)
                {
                    Log.Info($"Camera: {frames.Count} frames {frames.Width}x{frames.Height}");
                    CameraReady?.Invoke(frames);
                }
            }
            catch (Exception ex) { Log.Info("Camera frame handling failed: " + ex.Message); }
        });
    }

    private void SendStatus(bool heartbeat)
    {
        bool ready, detected;
        lock (_gate) { ready = _state == UnitState.Ready; detected = _ballDetected; }
        if (!_client.IsConnected) return;
        var msg = new ShotMessage
        {
            DeviceID = _s.DeviceId,
            Units = "Yards",
            ShotNumber = _assembler.LastShotNumber,
            APIversion = "1",
            ShotDataOptions = new ShotOptionsMsg
            {
                ContainsBallData = false,
                ContainsClubData = false,
                LaunchMonitorIsReady = ready,
                LaunchMonitorBallDetected = detected,
                IsHeartBeat = heartbeat,
            },
        };
        _client.Send(msg);
    }

    // ---------- sim -> unit ----------

    private void OnSimConnection(bool connected)
    {
        lock (_gate) _simConnected = connected;
        RaiseStatus();
        if (connected) SendStatus(heartbeat: false);
    }

    private void OnPlayerInfo(string handed, string club)
    {
        lock (_gate)
        {
            if (_s.AutoSwitchHand && handed.Length > 0)
                _desiredRight = !handed.StartsWith("L", StringComparison.OrdinalIgnoreCase);

            if (_s.AutoSwitchClub && club.Length > 0)
            {
                string c = club.Trim().ToUpperInvariant();
                if (c == "PT" || c.Contains("PUTT")) _desiredClub = _s.PutterClub;
                else if (c == "DR" || c == "D" || c.Contains("DRIVER")) _desiredClub = _s.DriverClub;
                else _desiredClub = _s.IronClub;
            }
        }
        ApplyDesired();
    }

    /// <summary>Sends hand/club to the unit once it is ready, and only when the value actually changed.</summary>
    private void ApplyDesired()
    {
        bool? right = null;
        int? club = null;
        lock (_gate)
        {
            if (_state != UnitState.Ready) return;
            if (_desiredRight is bool r && _appliedRight != r) { right = r; _appliedRight = r; }
            if (_desiredClub is int c && _appliedClub != c) { club = c; _appliedClub = c; }
        }
        try
        {
            if (right is bool rr) { _device.SetHand(rr); Log.Info($"Hand set to {(rr ? "right" : "left")}"); }
            if (club is int cc) { _device.SetClub(cc); Log.Info($"Club mode set to {cc}" + (cc == _s.PutterClub ? " (putter)" : "")); }
        }
        catch (Exception ex) { Log.Info("Setting hand/club failed: " + ex.Message); }
    }

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(StatusText); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _heartbeat?.Dispose();
        _assembler.Dispose();
        _client.Dispose();
        _device.Dispose();
        _relay?.Dispose();
        _cts.Dispose();
    }
}
