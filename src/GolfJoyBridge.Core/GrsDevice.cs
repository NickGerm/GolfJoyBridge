using System.Runtime.InteropServices;

namespace GolfJoyBridge;

/// <summary>
/// Talks to the unit through its own GRS.dll (loaded from a path the user chooses).
/// Callback shapes were worked out from the decompiled DLL and live logs:
///   shot : 8 float slots, [0]=ball speed m/s, [1]=launch angle, [2]=launch direction, [3]=backspin rpm, [4]=sidespin rpm
///   club : 8 float slots, [0]=club speed m/s, [1]=path, [2]=face, [3]=attack angle   (-9999 = not measured)
///   image: (pointer, width, height, frame count, type, byte size)
/// </summary>
public sealed class GrsDevice : ILaunchDevice
{
    // ---- native function shapes ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn([MarshalAs(UnmanagedType.LPStr)] string ip);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetIntFn(int v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetUIntFn(uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetIntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetLogDirFn([MarshalAs(UnmanagedType.LPStr)] string dir, int len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetStrFn(IntPtr buffer, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RegFn(IntPtr callback);

    // ---- callback shapes ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Float8Cb(float a, float b, float c, float d, float e, float f, float g, float h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Int4Cb(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ImageCb(IntPtr ptr, int width, int height, int count, int type, uint size);

    private readonly string _dllPath;
    private readonly string _unitIp;
    private readonly string? _sdkLogDir;

    private IntPtr _lib;
    private bool _started;
    private InitFn? _init;
    private VoidFn? _shutDown;
    private SetIntFn? _setClub;
    private SetUIntFn? _setHand;
    private GetIntFn? _getHand;
    private GetIntFn? _getBattery;
    private GetStrFn? _getFirmware;
    private GetStrFn? _getSensorType;

    // keep delegates alive for as long as native code may call them
    private readonly List<Delegate> _keepAlive = new();

    public event Action<SpicaBall>? Ball;
    public event Action<SpicaClub>? Club;
    public event Action<int>? BallState;
    public event Action<RawImage>? Image;
    public event Action<bool>? Connection;

    public GrsDevice(string dllPath, string unitIp, string? sdkLogDir)
    {
        _dllPath = dllPath;
        _unitIp = unitIp;
        _sdkLogDir = string.IsNullOrWhiteSpace(sdkLogDir) ? null : sdkLogDir;
    }

    private T Fn<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    private void Register(string exportName, Delegate callback)
    {
        _keepAlive.Add(callback);
        Fn<RegFn>(exportName)(Marshal.GetFunctionPointerForDelegate(callback));
    }

    public bool Start(out string error)
    {
        error = "";
        try
        {
            if (!Environment.Is64BitProcess)
            {
                error = "This program must run as a 64-bit process (GRS.dll is 64-bit).";
                return false;
            }
            string full = Path.GetFullPath(_dllPath);
            if (!File.Exists(full))
            {
                error = $"GRS.dll not found at: {full}\nSet DllPath in GolfJoyBridge.ini.";
                return false;
            }

            Win32.SetDllDirectory(Path.GetDirectoryName(full)); // so the DLL finds its own neighbours
            _lib = NativeLibrary.Load(full);

            _init = Fn<InitFn>("grs_Init");
            _shutDown = Fn<VoidFn>("grs_ShutDown");
            _setClub = Fn<SetIntFn>("grs_SetClub");
            _setHand = Fn<SetUIntFn>("grs_SetLRState");
            _getHand = Fn<GetIntFn>("grs_GetLRState");
            _getBattery = Fn<GetIntFn>("grs_GetBatteryLevel");
            _getFirmware = Fn<GetStrFn>("grs_GetFirmwareVerNum");
            _getSensorType = Fn<GetStrFn>("grs_GetSensorType");

            // Every callback is registered (as the working test script did); unused ones just log at debug level.
            Register("grs_RegShotDataCallback", new Float8Cb(OnShot));
            Register("grs_RegClubDataCallback", new Float8Cb(OnClub));
            Register("grs_RegBallStateCallback", new Int4Cb(OnBallState));
            Register("grs_RegClubImageCallback", new ImageCb(OnImage));
            Register("grs_RegSensorStateCallback", new Int4Cb(OnSensorState));
            Register("grs_RegSpinDataCallback", new Float8Cb((a, b, c, d, e, f, g, h) => { }));
            Register("grs_RegClubData2Callback", new Float8Cb((a, b, c, d, e, f, g, h) => { }));
            Register("grs_RegCarryDataCallback", new Float8Cb((a, b, c, d, e, f, g, h) => { }));
            Register("grs_RegSensingAreaCallback", new Int4Cb((a, b, c, d) => { }));
            Register("grs_RegNBallsAreaCallback", new Int4Cb((a, b, c, d) => { }));
            Register("grs_RegBallAtTeeCallback", new Int4Cb((a, b, c, d) => { }));
            Register("grs_RegShotTimeCallback", new Int4Cb((a, b, c, d) => { }));
            Register("grs_RegSensorExceptionCallback", new Int4Cb(OnException));
            Register("grs_RegAuthDaysCallback", new Int4Cb((a, b, c, d) => { }));

            if (_sdkLogDir != null)
            {
                Directory.CreateDirectory(_sdkLogDir);
                Fn<SetLogDirFn>("grs_SetSensorLogDir")(_sdkLogDir, _sdkLogDir.Length);
            }

            int rc = _init(_unitIp);
            if (rc != 0)
            {
                error = $"grs_Init({_unitIp}) returned {rc}.";
                return false;
            }
            _started = true;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ---- callbacks (run on the DLL's own threads: never throw, never block) ----

    private void OnShot(float a, float b, float c, float d, float e, float f, float g, float h)
    {
        try { Ball?.Invoke(new SpicaBall(a, b, c, d, e)); }
        catch (Exception ex) { Log.Info("Shot handler failed: " + ex.Message); }
    }

    private void OnClub(float a, float b, float c, float d, float e, float f, float g, float h)
    {
        try { Club?.Invoke(new SpicaClub(a, b, c, d)); }
        catch (Exception ex) { Log.Info("Club handler failed: " + ex.Message); }
    }

    // From the decompiled DLL: the first device heartbeat calls this with 0 ("connected");
    // if no heartbeat arrives for 6 seconds it calls it with 1 ("device heart beat time out").
    private void OnSensorState(IntPtr a, IntPtr b, IntPtr c, IntPtr d)
    {
        try
        {
            int state = unchecked((int)a.ToInt64());
            Connection?.Invoke(state == 0);
        }
        catch (Exception ex) { Log.Info("Sensor-state handler failed: " + ex.Message); }
    }

    private void OnBallState(IntPtr a, IntPtr b, IntPtr c, IntPtr d)
    {
        try { BallState?.Invoke(unchecked((int)a.ToInt64())); }
        catch (Exception ex) { Log.Info("Ball-state handler failed: " + ex.Message); }
    }

    private void OnException(IntPtr a, IntPtr b, IntPtr c, IntPtr d) =>
        Log.Info($"Unit reported an exception: {a:X} {b:X} {c:X} {d:X}");

    private void OnImage(IntPtr ptr, int w, int h, int cnt, int type, uint size)
    {
        try
        {
            if (ptr == IntPtr.Zero || w <= 0 || h <= 0 || cnt <= 0 || size == 0 || size > 64_000_000) return;
            long frameBytes = (long)w * h * cnt;
            int bpp = size % frameBytes == 0 ? (int)(size / frameBytes) : 1;
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, (int)size);
            Image?.Invoke(new RawImage(w, h, cnt, bpp, data));
        }
        catch (Exception ex) { Log.Info("Image handler failed: " + ex.Message); }
    }

    // ---- commands ----

    public int GetHand()
    {
        try { return _getHand?.Invoke() ?? -1; } catch { return -1; }
    }

    public void SetHand(bool right) => _setHand?.Invoke(right ? 1u : 0u);

    public void SetClub(int clubNumber) => _setClub?.Invoke(clubNumber);

    private string ReadString(GetStrFn? fn)
    {
        if (fn == null) return "";
        IntPtr buf = Marshal.AllocHGlobal(256);
        try
        {
            for (int i = 0; i < 256; i++) Marshal.WriteByte(buf, i, 0);
            fn(buf, 255);
            return Marshal.PtrToStringAnsi(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public string Describe()
    {
        try
        {
            int battery = _getBattery?.Invoke() ?? -1;
            return $"{ReadString(_getSensorType)} fw {ReadString(_getFirmware)}, battery {battery}%";
        }
        catch (Exception ex) { return "(could not read unit info: " + ex.Message + ")"; }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { _shutDown?.Invoke(); } catch { }
    }

    public void Dispose() => Stop();
}
