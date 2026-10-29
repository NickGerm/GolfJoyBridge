using System.Collections.Generic;
using System.Globalization;

namespace GolfJoyBridge;

/// <summary>Minimal INI reader: [sections], key = value, ';' or '#' comments.</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        string section = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            // strip trailing comment that is separated by whitespace: "value ; comment"
            int c = IndexOfInlineComment(value);
            if (c >= 0) value = value[..c].Trim();
            ini._values[section + "." + key] = value;
        }
        return ini;
    }

    private static int IndexOfInlineComment(string v)
    {
        for (int i = 1; i < v.Length; i++)
            if ((v[i] == ';' || v[i] == '#') && char.IsWhiteSpace(v[i - 1])) return i;
        return -1;
    }

    public string? Get(string section, string key) =>
        _values.TryGetValue(section + "." + key, out var v) ? v : null;
}

public enum CameraMode { Off, Loop, Popup }
public enum ConnectionMode { Wired, Wireless }

/// <summary>All user options. Loaded once at start-up from GolfJoyBridge.ini.</summary>
public sealed class Settings
{
    // [Unit]
    public string DllPath { get; set; } = "GRS.dll";
    public ConnectionMode Mode { get; set; } = ConnectionMode.Wired;
    public string UnitIp { get; set; } = "192.168.147.147";
    /// <summary>Wireless unit's own IP on your Wi-Fi network (give it a reserved/static DHCP lease). Required when Mode = Wireless.</summary>
    public string WirelessUnitIp { get; set; } = "";
    public string SdkLogFolder { get; set; } = "sdklog";
    public string DefaultHand { get; set; } = "right";          // right | left | none

    // [Sim]
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 921;
    public string DeviceId { get; set; } = "GolfJoyBridge";
    public double HeartbeatSeconds { get; set; } = 5;
    public double ReconnectSeconds { get; set; } = 3;
    public int ClubWaitMs { get; set; } = 1500;
    public bool AutoSwitchClub { get; set; } = true;
    public bool AutoSwitchHand { get; set; } = true;
    public int PutterClub { get; set; } = 45;
    public int DriverClub { get; set; } = 1;
    public int IronClub { get; set; } = 10;
    public bool SendClubData { get; set; } = true;
    public bool InvertDirection { get; set; }
    public bool InvertSideSpin { get; set; }

    // [Camera]
    public CameraMode CameraMode { get; set; } = CameraMode.Loop;
    public double PlaybackFps { get; set; } = 12;
    public double Gamma { get; set; } = 0.6;
    public int Monitor { get; set; } = 0;                        // 0 = primary, 1.. = Windows monitor order
    public int LoopX { get; set; } = -1;
    public int LoopY { get; set; } = -1;
    public int LoopWidth { get; set; } = 560;
    public int LoopHeight { get; set; } = 320;
    public bool LoopClickThrough { get; set; }
    public double PopupDelaySeconds { get; set; } = 2;
    public int PopupRepeats { get; set; } = 3;
    public int PopupWidth { get; set; } = 0;                     // 0 = automatic
    public int PopupHeight { get; set; } = 0;
    public int PopupX { get; set; } = -1;
    public int PopupY { get; set; } = -1;
    public bool PopupTakeFocus { get; set; }
    public bool PopupClickToClose { get; set; } = true;

    // [Saving]
    public bool SaveFrames { get; set; }
    public string SaveFolder { get; set; } = "shots";

    public static string ResolvePath(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));

    /// <summary>Loads the INI. Creates it with defaults and comments if it does not exist.</summary>
    public static Settings Load(string iniPath, Action<string>? warn = null)
    {
        warn ??= _ => { };
        if (!File.Exists(iniPath))
        {
            try { File.WriteAllText(iniPath, DefaultIniText.Replace("\r\n", "\n").Replace("\n", "\r\n")); }
            catch (Exception ex) { warn($"Could not create {iniPath}: {ex.Message}. Using defaults."); }
        }

        IniFile ini;
        try { ini = IniFile.Parse(File.Exists(iniPath) ? File.ReadAllText(iniPath) : DefaultIniText); }
        catch (Exception ex) { warn($"Could not read {iniPath}: {ex.Message}. Using defaults."); ini = IniFile.Parse(DefaultIniText); }

        var d = new Settings();
        var s = new Settings
        {
            DllPath = Str(ini, "Unit", "DllPath", d.DllPath),
            UnitIp = Str(ini, "Unit", "UnitIp", d.UnitIp),
            WirelessUnitIp = Str(ini, "Unit", "WirelessUnitIp", d.WirelessUnitIp),
            SdkLogFolder = Str(ini, "Unit", "SdkLogFolder", d.SdkLogFolder),
            DefaultHand = Choice(ini, "Unit", "DefaultHand", d.DefaultHand, warn, "right", "left", "none"),

            Host = Str(ini, "Sim", "Host", d.Host),
            Port = Int(ini, "Sim", "Port", d.Port, 1, 65535, warn),
            DeviceId = Str(ini, "Sim", "DeviceId", d.DeviceId),
            HeartbeatSeconds = Dbl(ini, "Sim", "HeartbeatSeconds", d.HeartbeatSeconds, 1, 120, warn),
            ReconnectSeconds = Dbl(ini, "Sim", "ReconnectSeconds", d.ReconnectSeconds, 0.5, 120, warn),
            ClubWaitMs = Int(ini, "Sim", "ClubWaitMs", d.ClubWaitMs, 100, 10000, warn),
            AutoSwitchClub = Bool(ini, "Sim", "AutoSwitchClub", d.AutoSwitchClub, warn),
            AutoSwitchHand = Bool(ini, "Sim", "AutoSwitchHand", d.AutoSwitchHand, warn),
            PutterClub = Int(ini, "Sim", "PutterClub", d.PutterClub, 0, 1000, warn),
            DriverClub = Int(ini, "Sim", "DriverClub", d.DriverClub, 0, 1000, warn),
            IronClub = Int(ini, "Sim", "IronClub", d.IronClub, 0, 1000, warn),
            SendClubData = Bool(ini, "Sim", "SendClubData", d.SendClubData, warn),
            InvertDirection = Bool(ini, "Sim", "InvertDirection", d.InvertDirection, warn),
            InvertSideSpin = Bool(ini, "Sim", "InvertSideSpin", d.InvertSideSpin, warn),

            PlaybackFps = Dbl(ini, "Camera", "PlaybackFps", d.PlaybackFps, 1, 120, warn),
            Gamma = Dbl(ini, "Camera", "Gamma", d.Gamma, 0.2, 3.0, warn),
            Monitor = Int(ini, "Camera", "Monitor", d.Monitor, 0, 16, warn),
            LoopX = Int(ini, "Camera", "LoopX", d.LoopX, -1, 20000, warn),
            LoopY = Int(ini, "Camera", "LoopY", d.LoopY, -1, 20000, warn),
            LoopWidth = Int(ini, "Camera", "LoopWidth", d.LoopWidth, 64, 8000, warn),
            LoopHeight = Int(ini, "Camera", "LoopHeight", d.LoopHeight, 36, 8000, warn),
            LoopClickThrough = Bool(ini, "Camera", "LoopClickThrough", d.LoopClickThrough, warn),
            PopupDelaySeconds = Dbl(ini, "Camera", "PopupDelaySeconds", d.PopupDelaySeconds, 0, 600, warn),
            PopupRepeats = Int(ini, "Camera", "PopupRepeats", d.PopupRepeats, 1, 100, warn),
            PopupWidth = Int(ini, "Camera", "PopupWidth", d.PopupWidth, 0, 8000, warn),
            PopupHeight = Int(ini, "Camera", "PopupHeight", d.PopupHeight, 0, 8000, warn),
            PopupX = Int(ini, "Camera", "PopupX", d.PopupX, -1, 20000, warn),
            PopupY = Int(ini, "Camera", "PopupY", d.PopupY, -1, 20000, warn),
            PopupTakeFocus = Bool(ini, "Camera", "PopupTakeFocus", d.PopupTakeFocus, warn),
            PopupClickToClose = Bool(ini, "Camera", "PopupClickToClose", d.PopupClickToClose, warn),

            SaveFrames = Bool(ini, "Saving", "SaveFrames", d.SaveFrames, warn),
            SaveFolder = Str(ini, "Saving", "SaveFolder", d.SaveFolder),
        };

        string camMode = Choice(ini, "Camera", "Mode", "loop", warn, "off", "loop", "popup");
        s.CameraMode = camMode switch { "off" => CameraMode.Off, "popup" => CameraMode.Popup, _ => CameraMode.Loop };

        string connMode = Choice(ini, "Unit", "ConnectionMode", "wired", warn, "wired", "wireless");
        s.Mode = connMode == "wireless" ? ConnectionMode.Wireless : ConnectionMode.Wired;
        return s;
    }

    // ---- typed readers: bad values fall back to the default and warn ----

    private static string Str(IniFile ini, string sec, string key, string def)
    {
        var v = ini.Get(sec, key);
        return v ?? def;
    }

    private static string Choice(IniFile ini, string sec, string key, string def, Action<string> warn, params string[] allowed)
    {
        var v = ini.Get(sec, key);
        if (v == null) return def;
        v = v.Trim().ToLowerInvariant();
        if (Array.IndexOf(allowed, v) >= 0) return v;
        warn($"[{sec}] {key} = '{v}' is not one of ({string.Join(", ", allowed)}). Using '{def}'.");
        return def;
    }

    private static int Int(IniFile ini, string sec, string key, int def, int min, int max, Action<string> warn)
    {
        var v = ini.Get(sec, key);
        if (v == null || v.Length == 0) return def;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            warn($"[{sec}] {key} = '{v}' is not a whole number. Using {def}.");
            return def;
        }
        if (n < min || n > max)
        {
            warn($"[{sec}] {key} = {n} is outside {min}..{max}. Using {def}.");
            return def;
        }
        return n;
    }

    private static double Dbl(IniFile ini, string sec, string key, double def, double min, double max, Action<string> warn)
    {
        var v = ini.Get(sec, key);
        if (v == null || v.Length == 0) return def;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || double.IsNaN(n))
        {
            warn($"[{sec}] {key} = '{v}' is not a number. Using {def.ToString(CultureInfo.InvariantCulture)}.");
            return def;
        }
        if (n < min || n > max)
        {
            warn($"[{sec}] {key} = {n.ToString(CultureInfo.InvariantCulture)} is outside {min}..{max}. Using {def.ToString(CultureInfo.InvariantCulture)}.");
            return def;
        }
        return n;
    }

    private static bool Bool(IniFile ini, string sec, string key, bool def, Action<string> warn)
    {
        var v = ini.Get(sec, key);
        if (v == null || v.Length == 0) return def;
        switch (v.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "on": case "1": return true;
            case "false": case "no": case "off": case "0": return false;
        }
        warn($"[{sec}] {key} = '{v}' is not true/false. Using {def.ToString().ToLowerInvariant()}.");
        return def;
    }

    /// <summary>
    /// Writes this object back to the ini file, keeping the existing file's comments and layout
    /// (or the default template's, if the file does not exist yet). Only recognised "Key = value"
    /// lines are touched; everything else (comments, blank lines, section headers) is left as is.
    /// </summary>
    public void Save(string iniPath)
    {
        string template;
        try { template = File.Exists(iniPath) ? File.ReadAllText(iniPath) : DefaultIniText; }
        catch { template = DefaultIniText; }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DllPath"] = DllPath,
            ["ConnectionMode"] = Mode == ConnectionMode.Wireless ? "wireless" : "wired",
            ["UnitIp"] = UnitIp,
            ["WirelessUnitIp"] = WirelessUnitIp,
            ["SdkLogFolder"] = SdkLogFolder,
            ["DefaultHand"] = DefaultHand,

            ["Host"] = Host,
            ["Port"] = Port.ToString(CultureInfo.InvariantCulture),
            ["DeviceId"] = DeviceId,
            ["HeartbeatSeconds"] = HeartbeatSeconds.ToString(CultureInfo.InvariantCulture),
            ["ReconnectSeconds"] = ReconnectSeconds.ToString(CultureInfo.InvariantCulture),
            ["ClubWaitMs"] = ClubWaitMs.ToString(CultureInfo.InvariantCulture),
            ["AutoSwitchClub"] = B(AutoSwitchClub),
            ["AutoSwitchHand"] = B(AutoSwitchHand),
            ["PutterClub"] = PutterClub.ToString(CultureInfo.InvariantCulture),
            ["DriverClub"] = DriverClub.ToString(CultureInfo.InvariantCulture),
            ["IronClub"] = IronClub.ToString(CultureInfo.InvariantCulture),
            ["SendClubData"] = B(SendClubData),
            ["InvertDirection"] = B(InvertDirection),
            ["InvertSideSpin"] = B(InvertSideSpin),

            ["PlaybackFps"] = PlaybackFps.ToString(CultureInfo.InvariantCulture),
            ["Gamma"] = Gamma.ToString(CultureInfo.InvariantCulture),
            ["Monitor"] = Monitor.ToString(CultureInfo.InvariantCulture),
            ["LoopX"] = LoopX.ToString(CultureInfo.InvariantCulture),
            ["LoopY"] = LoopY.ToString(CultureInfo.InvariantCulture),
            ["LoopWidth"] = LoopWidth.ToString(CultureInfo.InvariantCulture),
            ["LoopHeight"] = LoopHeight.ToString(CultureInfo.InvariantCulture),
            ["LoopClickThrough"] = B(LoopClickThrough),
            ["PopupDelaySeconds"] = PopupDelaySeconds.ToString(CultureInfo.InvariantCulture),
            ["PopupRepeats"] = PopupRepeats.ToString(CultureInfo.InvariantCulture),
            ["PopupWidth"] = PopupWidth.ToString(CultureInfo.InvariantCulture),
            ["PopupHeight"] = PopupHeight.ToString(CultureInfo.InvariantCulture),
            ["PopupX"] = PopupX.ToString(CultureInfo.InvariantCulture),
            ["PopupY"] = PopupY.ToString(CultureInfo.InvariantCulture),
            ["PopupTakeFocus"] = B(PopupTakeFocus),
            ["PopupClickToClose"] = B(PopupClickToClose),

            ["SaveFrames"] = B(SaveFrames),
            ["SaveFolder"] = SaveFolder,
        };
        values["Mode"] = CameraMode switch { CameraMode.Off => "off", CameraMode.Popup => "popup", _ => "loop" };

        // Which section each key lives in. Used only for keys the template turns out not to have
        // (e.g. a file saved by an older version, before a setting existed) - those are appended
        // under their section header instead of silently being dropped.
        var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DllPath"] = "Unit", ["ConnectionMode"] = "Unit", ["UnitIp"] = "Unit", ["WirelessUnitIp"] = "Unit",
            ["SdkLogFolder"] = "Unit", ["DefaultHand"] = "Unit",
            ["Host"] = "Sim", ["Port"] = "Sim", ["DeviceId"] = "Sim", ["HeartbeatSeconds"] = "Sim",
            ["ReconnectSeconds"] = "Sim", ["ClubWaitMs"] = "Sim", ["AutoSwitchClub"] = "Sim", ["AutoSwitchHand"] = "Sim",
            ["PutterClub"] = "Sim", ["DriverClub"] = "Sim", ["IronClub"] = "Sim", ["SendClubData"] = "Sim",
            ["InvertDirection"] = "Sim", ["InvertSideSpin"] = "Sim",
            ["Mode"] = "Camera", ["PlaybackFps"] = "Camera", ["Gamma"] = "Camera", ["Monitor"] = "Camera",
            ["LoopX"] = "Camera", ["LoopY"] = "Camera", ["LoopWidth"] = "Camera", ["LoopHeight"] = "Camera",
            ["LoopClickThrough"] = "Camera", ["PopupDelaySeconds"] = "Camera", ["PopupRepeats"] = "Camera",
            ["PopupWidth"] = "Camera", ["PopupHeight"] = "Camera", ["PopupX"] = "Camera", ["PopupY"] = "Camera",
            ["PopupTakeFocus"] = "Camera", ["PopupClickToClose"] = "Camera",
            ["SaveFrames"] = "Saving", ["SaveFolder"] = "Saving",
        };

        string text = template;
        var missing = new List<string>();
        foreach (var kv in values)
        {
            // Matches "Key = value" (any amount of space around '=') at the start of a line, keeps the
            // key/spacing, replaces the value. Keeps any trailing whitespace before a same-line comment,
            // so "922 ; note" becomes "921 ; note", not "921; note".
            var pattern = new System.Text.RegularExpressions.Regex(
                "^([ \t]*" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + "[ \t]*=[ \t]*)[^\r\n;#]*?([ \t]*)(?=[;#]|\r?$)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            if (pattern.IsMatch(text))
                text = pattern.Replace(text, m => m.Groups[1].Value + kv.Value + m.Groups[2].Value, 1);
            else
                missing.Add(kv.Key);
        }

        foreach (var key in missing)
        {
            string sec = section.TryGetValue(key, out var s2) ? s2 : "Unit";
            string line = key + " = " + values[key];
            var header = new System.Text.RegularExpressions.Regex(
                @"^\[" + System.Text.RegularExpressions.Regex.Escape(sec) + @"\][ \t]*\r?\n",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            var m = header.Match(text);
            text = m.Success
                ? text.Insert(m.Index + m.Length, line + "\n")
                : text.TrimEnd() + "\n\n[" + sec + "]\n" + line + "\n";
        }

        File.WriteAllText(iniPath, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    private static string B(bool v) => v ? "true" : "false";

    public const string DefaultIniText = """
; GolfJoyBridge settings
; Edit this file, save it, then restart the program (tray icon > Restart).
; Lines starting with ; are comments. Paths may be absolute or relative to the program folder.

[Unit]
; Path to the launch monitor's GRS.dll (copy it from the official app's install folder).
DllPath = GRS.dll
; IP address of the unit. Hard-wired Spica 3 units use 192.168.147.147.
; Your PC's network adapter must be on the same subnet (for example 192.168.147.2, mask 255.255.255.0).
UnitIp = 192.168.147.147
; Folder the DLL may write its own logs/images to. Leave blank to not set one.
; If no camera images show up, make sure this is NOT blank.
SdkLogFolder = sdklog
; Hand to use at start-up until the sim says otherwise: right, left or none (leave the unit as it is).
DefaultHand = right
; How this PC reaches the unit: wired or wireless. Change this with the Settings window (tray icon) for a clean restart.
ConnectionMode = wired
; Only used when ConnectionMode = wireless: the unit's own IP address on your Wi-Fi network.
; Give the unit a reserved/static address on your router so this does not change. Required for wireless.
WirelessUnitIp = 

[Sim]
; Open Connect (GSPro API v1) server. GSPro, Muni, OpenGolfSim, ShotForge etc. use 127.0.0.1 port 921.
Host = 127.0.0.1
Port = 921
; Name this program reports to the sim.
DeviceId = GolfJoyBridge
; Seconds between "still alive" messages to the sim.
HeartbeatSeconds = 5
; Seconds to wait before retrying when the sim is not running or the connection drops.
ReconnectSeconds = 3
; After a shot, how long to wait for club data before sending the ball data on its own (milliseconds).
ClubWaitMs = 1500
; Let the sim switch the unit between putting and full-swing mode (true/false).
AutoSwitchClub = true
; Let the sim switch the unit between right and left handed (true/false).
AutoSwitchHand = true
; Club numbers sent to the unit. 45 = putter. 1 = driver. Any other number = iron.
PutterClub = 45
DriverClub = 1
IronClub = 10
; Send club speed / path / attack angle to the sim on full swings (true/false).
SendClubData = true
; If shots go left when they should go right, flip these.
; InvertDirection flips launch direction (and club path / face). InvertSideSpin flips sidespin and spin axis.
InvertDirection = false
InvertSideSpin = false

[Camera]
; What to do with the shot camera pictures: off, loop or popup.
;   loop  = one small always-on-top window that keeps replaying the latest shot (never takes focus).
;   popup = after the shot, wait PopupDelaySeconds, show the shot PopupRepeats times, then close.
Mode = loop
; Playback speed in frames per second. A shot is about 25 frames, so 12 is about 2 seconds, slow motion.
PlaybackFps = 12
; Brightness curve. 1.0 = original (dark). Smaller = brighter. 0.6 is a good start.
Gamma = 0.6
; Which monitor to use: 0 = primary, 1 = first monitor, 2 = second, ...
Monitor = 0

; ---- loop window ----
; X and Y are pixels from the top-left of the chosen monitor. -1 = bottom-right corner.
LoopX = -1
LoopY = -1
LoopWidth = 560
LoopHeight = 320
; true = mouse clicks pass through the window to the sim underneath.
LoopClickThrough = false

; ---- popup window ----
; Seconds after the camera pictures arrive before the popup opens.
PopupDelaySeconds = 2
; How many times the shot plays before the popup closes.
PopupRepeats = 3
; Size in pixels. 0 = automatic (half the monitor width).
PopupWidth = 0
PopupHeight = 0
; Position in pixels from the top-left of the monitor. -1 = centered.
PopupX = -1
PopupY = -1
; true = the popup takes keyboard focus while showing, then gives it back to the sim when it closes.
; false = the popup shows on top without taking focus (nothing to give back).
PopupTakeFocus = false
; Click on the popup to close it early.
PopupClickToClose = true

[Saving]
; Save every shot's camera frames as PNG files (true/false).
SaveFrames = false
SaveFolder = shots
""";
}
