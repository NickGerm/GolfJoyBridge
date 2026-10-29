using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GolfJoyBridge.App;

/// <summary>
/// Shows a shot's camera frames according to [Camera] Mode:
///   loop  - one small always-on-top window that keeps replaying the newest shot (never takes focus)
///   popup - after PopupDelaySeconds a window plays the shot PopupRepeats times, closes, and (optionally) hands focus back
/// </summary>
public sealed class CameraManager
{
    private readonly Settings _s;
    private readonly string _baseDir;
    private readonly Dispatcher _ui;

    private CameraWindow? _loopWindow;
    private CameraWindow? _popup;
    private CameraWindow? _preview;
    private DispatcherTimer? _delayTimer;
    private IntPtr _previousForeground;
    private BitmapSource[]? _lastFrames;

    public CameraManager(Settings settings, string baseDir, Dispatcher ui)
    {
        _s = settings;
        _baseDir = baseDir;
        _ui = ui;
    }

    // ------------------------------------------------------------------ public API

    public void Start()
    {
        if (_s.CameraMode != CameraMode.Loop) return;
        _ui.BeginInvoke(new Action(() =>
        {
            _loopWindow = new CameraWindow(noActivate: true, clickThrough: _s.LoopClickThrough, placeholder: "Waiting for the first shot...");
            ShowAt(_loopWindow, _s.LoopX, _s.LoopY, _s.LoopWidth, _s.LoopHeight, centerByDefault: false);
        }));
    }

    /// <summary>Called from any thread when a new shot's frames are ready.</summary>
    public void OnFrames(FrameSet frames)
    {
        if (_s.CameraMode == CameraMode.Off) return;

        BitmapSource[] bitmaps;
        try { bitmaps = ToBitmaps(frames); }
        catch (Exception ex) { Log.Info("Could not build camera images: " + ex.Message); return; }

        if (_s.SaveFrames) Task.Run(() => SaveFrames(bitmaps));

        _ui.BeginInvoke(new Action(() =>
        {
            _lastFrames = bitmaps;
            if (_s.CameraMode == CameraMode.Loop) PlayInLoopWindow(bitmaps);
            else StartPopupFlow(bitmaps, _s.PopupDelaySeconds);
        }));
    }

    /// <summary>Plays the most recent shot again (tray menu).</summary>
    public void Replay()
    {
        _ui.BeginInvoke(new Action(() =>
        {
            if (_lastFrames == null) return;
            if (_s.CameraMode == CameraMode.Loop) PlayInLoopWindow(_lastFrames);
            else if (_s.CameraMode == CameraMode.Popup) StartPopupFlow(_lastFrames, 0);
        }));
    }

    public void Shutdown()
    {
        _ui.Invoke(new Action(() =>
        {
            CancelPopup(restoreFocus: false);
            _loopWindow?.Close();
            _loopWindow = null;
            _preview?.Close();
            _preview = null;
        }));
    }

    /// <summary>
    /// Called by the settings window right after it changes <see cref="Settings.CameraMode"/> (or the
    /// loop window's own settings), so the real loop window appears, disappears, or moves immediately
    /// instead of waiting for the next shot. Safe to call any time, from any thread.
    /// </summary>
    public void ApplyLiveCameraSettings()
    {
        _ui.BeginInvoke(new Action(() =>
        {
            if (_s.CameraMode != CameraMode.Loop)
            {
                _loopWindow?.Close();
                _loopWindow = null;
                return;
            }
            if (_loopWindow == null)
            {
                _loopWindow = new CameraWindow(noActivate: true, clickThrough: _s.LoopClickThrough,
                    placeholder: _lastFrames == null ? "Waiting for the first shot..." : "");
                if (_lastFrames != null) _loopWindow.Play(_lastFrames, _s.PlaybackFps, loops: 0);
            }
            ShowAt(_loopWindow, _s.LoopX, _s.LoopY, _s.LoopWidth, _s.LoopHeight, centerByDefault: false);
        }));
    }

    // ------------------------------------------------------------------ settings-window preview
    // A window that mirrors wherever the sliders currently say, so the person can see exactly where
    // it will land. Plays the last real shot on loop if there is one, otherwise a placeholder.

    public void StartPreview()
    {
        _ui.BeginInvoke(new Action(() =>
        {
            if (_preview != null) return;
            _preview = new CameraWindow(noActivate: true, clickThrough: false,
                placeholder: _lastFrames == null ? "Preview\n(shows your last shot once you've hit one)" : "");
            if (_lastFrames != null) _preview.Play(_lastFrames, _s.PlaybackFps, loops: 0);
            _preview.Show();
        }));
    }

    /// <summary>Moves/resizes the live preview window to match the sliders (x/y/width/height are raw settings values; x/y may be -1 for "default"). Reads the monitor from the shared Settings object, which the settings window updates live.</summary>
    public void UpdatePreview(int x, int y, int width, int height, bool centerByDefault)
    {
        _ui.BeginInvoke(new Action(() =>
        {
            if (_preview == null) return;
            ShowAt(_preview, x, y, width, height, centerByDefault);
        }));
    }

    public void StopPreview()
    {
        _ui.BeginInvoke(new Action(() =>
        {
            _preview?.Close();
            _preview = null;
        }));
    }

    // ------------------------------------------------------------------ loop mode

    private void PlayInLoopWindow(BitmapSource[] frames)
    {
        if (_loopWindow == null)
        {
            _loopWindow = new CameraWindow(noActivate: true, clickThrough: _s.LoopClickThrough, placeholder: "");
            ShowAt(_loopWindow, _s.LoopX, _s.LoopY, _s.LoopWidth, _s.LoopHeight, centerByDefault: false);
        }
        _loopWindow.Play(frames, _s.PlaybackFps, loops: 0);
    }

    // ------------------------------------------------------------------ popup mode

    private void StartPopupFlow(BitmapSource[] frames, double delaySeconds)
    {
        CancelPopup(restoreFocus: true); // a newer shot replaces whatever is showing or waiting

        if (delaySeconds <= 0)
        {
            ShowPopup(frames);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_delayTimer, timer)) _delayTimer = null;
            ShowPopup(frames);
        };
        _delayTimer = timer;
        timer.Start();
    }

    private void ShowPopup(BitmapSource[] frames)
    {
        if (frames.Length == 0) return;

        _previousForeground = Win32.GetForegroundWindow();

        var popup = new CameraWindow(noActivate: !_s.PopupTakeFocus, clickThrough: false, placeholder: "");
        _popup = popup;

        int frameW = frames[0].PixelWidth, frameH = frames[0].PixelHeight;
        var screen = ScreenHelper.Pick(_s.Monitor);
        int w = _s.PopupWidth > 0 ? _s.PopupWidth : screen.Bounds.Width / 2;
        int h = _s.PopupHeight > 0 ? _s.PopupHeight : (int)Math.Round(w * (double)frameH / Math.Max(1, frameW));

        popup.PlaybackFinished += () => ClosePopup(popup);
        if (_s.PopupClickToClose)
            popup.MouseLeftButtonDown += (_, _) => ClosePopup(popup);

        ShowAt(popup, _s.PopupX, _s.PopupY, w, h, centerByDefault: true);
        if (_s.PopupTakeFocus) popup.Activate();
        popup.Play(frames, _s.PlaybackFps, _s.PopupRepeats);
    }

    private void ClosePopup(CameraWindow which)
    {
        if (!ReferenceEquals(_popup, which)) return;
        CancelPopup(restoreFocus: true);
    }

    /// <summary>Stops a waiting or visible popup. If it had keyboard focus, gives focus back to the window that had it before.</summary>
    private void CancelPopup(bool restoreFocus)
    {
        if (_delayTimer != null)
        {
            _delayTimer.Stop();
            _delayTimer = null;
        }

        var popup = _popup;
        if (popup == null) return;
        _popup = null;

        IntPtr popupHandle = IntPtr.Zero;
        bool hadFocus = false;
        try
        {
            popupHandle = popup.Handle;
            hadFocus = Win32.GetForegroundWindow() == popupHandle;
        }
        catch { }

        try { popup.Close(); } catch { }

        // Only hand focus back if the popup really had it (the user may have clicked elsewhere meanwhile).
        if (restoreFocus && _s.PopupTakeFocus && hadFocus && _previousForeground != IntPtr.Zero && _previousForeground != popupHandle)
        {
            if (!Win32.ForceForeground(_previousForeground))
                Log.Info("Could not hand focus back to the previous window.");
        }
    }

    // ------------------------------------------------------------------ placement

    private void ShowAt(CameraWindow win, int xSetting, int ySetting, int width, int height, bool centerByDefault)
    {
        var b = ScreenHelper.Pick(_s.Monitor).Bounds; // pixels
        width = Math.Min(width, b.Width);
        height = Math.Min(height, b.Height);

        int x, y;
        if (xSetting >= 0) x = b.Left + xSetting;
        else
        {
            var (dx, _) = centerByDefault ? ScreenHelper.DefaultPopupPosition(b, width, height) : ScreenHelper.DefaultLoopPosition(b, width, height);
            x = b.Left + dx;
        }
        if (ySetting >= 0) y = b.Top + ySetting;
        else
        {
            var (_, dy) = centerByDefault ? ScreenHelper.DefaultPopupPosition(b, width, height) : ScreenHelper.DefaultLoopPosition(b, width, height);
            y = b.Top + dy;
        }

        IntPtr hwnd = win.Handle;       // creates the native window without showing it
        win.Show();
        Win32.PlaceTopmost(hwnd, x, y, width, height, show: true);
    }

    // ------------------------------------------------------------------ frames

    private static BitmapSource[] ToBitmaps(FrameSet fs)
    {
        var result = new BitmapSource[fs.Count];
        for (int i = 0; i < fs.Count; i++)
        {
            var bmp = BitmapSource.Create(fs.Width, fs.Height, 96, 96, PixelFormats.Gray8, null, fs.Frames[i], fs.Width);
            bmp.Freeze(); // frozen images can be used from any thread
            result[i] = bmp;
        }
        return result;
    }

    private void SaveFrames(BitmapSource[] frames)
    {
        try
        {
            string root = Settings.ResolvePath(_baseDir, _s.SaveFolder);
            string dir = Path.Combine(root, "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(dir);
            for (int i = 0; i < frames.Length; i++)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frames[i]));
                using var file = File.Create(Path.Combine(dir, $"frame_{i:00}.png"));
                encoder.Save(file);
            }
            Log.Info("Saved camera frames to " + dir);
        }
        catch (Exception ex) { Log.Info("Saving camera frames failed: " + ex.Message); }
    }
}
