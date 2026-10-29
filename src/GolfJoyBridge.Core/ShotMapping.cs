using System.Globalization;

namespace GolfJoyBridge;

/// <summary>Ball data exactly as the unit's shot callback delivers it (SI-ish units).</summary>
public readonly record struct SpicaBall(float SpeedMps, float Vla, float Hla, float BackSpin, float SideSpin);

/// <summary>Club data exactly as the unit's club callback delivers it.</summary>
public readonly record struct SpicaClub(float SpeedMps, float Path, float Face, float Attack);

public sealed record MappedShot(ShotMessage Message, bool IsPutt, string Summary);

/// <summary>Converts unit data into the Open Connect shot message.</summary>
public static class ShotMapper
{
    public const double MpsToMph = 2.23694;

    /// <summary>The unit marks "not measured" with -9999, -99999 and similar large negatives.</summary>
    public static bool Valid(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v > -9000f;

    private static double R(double v, int digits = 2)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        return Math.Round(v, digits, MidpointRounding.AwayFromZero);
    }

    public static MappedShot Map(Settings s, SpicaBall b, SpicaClub? club, int shotNumber)
    {
        // The unit does not measure spin on putts (backspin/sidespin come back as -99999).
        bool isPutt = !Valid(b.BackSpin) || !Valid(b.SideSpin);

        double speedMph = Valid(b.SpeedMps) ? b.SpeedMps * MpsToMph : 0;
        double hla = Valid(b.Hla) ? b.Hla : 0;
        if (s.InvertDirection) hla = -hla;

        double vla = 0, back = 0, side = 0, total = 0, axis = 0;
        if (!isPutt)
        {
            vla = Valid(b.Vla) ? b.Vla : 0;
            back = b.BackSpin;
            side = s.InvertSideSpin ? -b.SideSpin : b.SideSpin;
            total = Math.Sqrt(back * back + side * side);
            axis = Math.Atan2(side, back) * 180.0 / Math.PI;
        }

        var msg = new ShotMessage
        {
            DeviceID = s.DeviceId,
            Units = "Yards",
            ShotNumber = shotNumber,
            APIversion = "1",
            BallData = new BallDataMsg
            {
                Speed = R(speedMph),
                HLA = R(hla),
                VLA = R(vla),
                BackSpin = R(back, 0),
                SideSpin = R(side, 0),
                TotalSpin = R(total, 0),
                SpinAxis = R(axis),
            },
            ShotDataOptions = new ShotOptionsMsg
            {
                ContainsBallData = true,
                ContainsClubData = false,
                LaunchMonitorIsReady = true,
                LaunchMonitorBallDetected = true,
                IsHeartBeat = false,
            },
        };

        string clubText = "";
        if (!isPutt && s.SendClubData && club is { } c && Valid(c.SpeedMps) && c.SpeedMps > 0)
        {
            double path = Valid(c.Path) ? c.Path : 0;
            double face = Valid(c.Face) ? c.Face : 0;
            if (s.InvertDirection) { path = -path; face = -face; }
            msg.ClubData = new ClubDataMsg
            {
                Speed = R(c.SpeedMps * MpsToMph),
                Path = R(path),
                FaceToTarget = R(face),
                AngleOfAttack = R(Valid(c.Attack) ? c.Attack : 0),
            };
            msg.ShotDataOptions.ContainsClubData = true;
            clubText = $" | club {msg.ClubData.Speed.ToString("0.0", CultureInfo.InvariantCulture)} mph, path {msg.ClubData.Path.ToString("0.0", CultureInfo.InvariantCulture)}, attack {msg.ClubData.AngleOfAttack.ToString("0.0", CultureInfo.InvariantCulture)}";
        }

        var inv = CultureInfo.InvariantCulture;
        string summary = isPutt
            ? $"PUTT #{shotNumber}: {msg.BallData.Speed.ToString("0.0", inv)} mph, direction {msg.BallData.HLA.ToString("0.0", inv)} deg"
            : $"SHOT #{shotNumber}: {msg.BallData.Speed.ToString("0.0", inv)} mph | VLA {msg.BallData.VLA.ToString("0.0", inv)} | HLA {msg.BallData.HLA.ToString("0.0", inv)} | back {msg.BallData.BackSpin.ToString("0", inv)} | side {msg.BallData.SideSpin.ToString("0", inv)} | total {msg.BallData.TotalSpin.ToString("0", inv)} | axis {msg.BallData.SpinAxis.ToString("0.0", inv)}{clubText}";

        return new MappedShot(msg, isPutt, summary);
    }
}

/// <summary>
/// The unit reports a shot as separate callbacks: ball data first, club data about a moment later.
/// This waits briefly for the club data so both can go out in a single Open Connect message.
/// </summary>
public sealed class ShotAssembler : IDisposable
{
    private readonly Settings _settings;
    private readonly object _gate = new();
    private SpicaBall? _pending;
    private Timer? _timer;
    private int _shotNumber;

    public event Action<MappedShot>? ShotReady;

    public ShotAssembler(Settings settings) => _settings = settings;

    public int LastShotNumber { get { lock (_gate) return _shotNumber; } }

    public void OnBall(SpicaBall ball)
    {
        SpicaBall? flush = null;
        lock (_gate)
        {
            if (_pending is { } older) flush = older; // a new shot arrived before the previous one was sent
            _pending = ball;
            _timer?.Dispose();
            _timer = new Timer(_ => OnTimeout(), null, _settings.ClubWaitMs, Timeout.Infinite);
        }
        if (flush is { } f) Emit(f, null);
    }

    public void OnClub(SpicaClub club)
    {
        SpicaBall? ball;
        lock (_gate)
        {
            ball = _pending;
            _pending = null;
            _timer?.Dispose();
            _timer = null;
        }
        if (ball is { } b) Emit(b, club);
    }

    private void OnTimeout()
    {
        SpicaBall? ball;
        lock (_gate)
        {
            ball = _pending;
            _pending = null;
            _timer?.Dispose();
            _timer = null;
        }
        if (ball is { } b) Emit(b, null);
    }

    private void Emit(SpicaBall ball, SpicaClub? club)
    {
        int n;
        lock (_gate) n = ++_shotNumber;
        var mapped = ShotMapper.Map(_settings, ball, club, n);
        try { ShotReady?.Invoke(mapped); }
        catch (Exception ex) { Log.Info("ShotReady handler failed: " + ex.Message); }
    }

    public void Dispose()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; _pending = null; }
    }
}
