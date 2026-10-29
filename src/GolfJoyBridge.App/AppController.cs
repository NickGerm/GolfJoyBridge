using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace GolfJoyBridge.App;

/// <summary>Owns the tray icon, the connector service and the camera windows.</summary>
public sealed class AppController : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly Settings _s;
    private readonly string _iniPath;
    private readonly string _baseDir;
    private readonly List<string> _startupWarnings;

    private ConnectorService? _service;
    private CameraManager? _camera;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _statusItem;
    private Drawing.Icon? _currentIcon;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private bool _failed;

    public AppController(System.Windows.Application app, Settings settings, string iniPath, string baseDir, List<string> startupWarnings)
    {
        _app = app;
        _s = settings;
        _iniPath = iniPath;
        _baseDir = baseDir;
        _startupWarnings = startupWarnings;
    }

    public void Start()
    {
        BuildTray();

        _camera = new CameraManager(_s, _baseDir, _app.Dispatcher);
        _camera.Start();

        string dll = Settings.ResolvePath(_baseDir, _s.DllPath);
        string? sdkLog = string.IsNullOrWhiteSpace(_s.SdkLogFolder) ? null : Settings.ResolvePath(_baseDir, _s.SdkLogFolder);
        // Wireless: the relay (started by ConnectorService) stands in for the unit at 127.0.0.1.
        string unitIp = _s.Mode == ConnectionMode.Wireless ? "127.0.0.1" : _s.UnitIp;
        var device = new GrsDevice(dll, unitIp, sdkLog);

        _service = new ConnectorService(_s, device);
        _service.StatusChanged += _ => _app.Dispatcher.BeginInvoke(new Action(UpdateStatus));
        _service.CameraReady += frames => _camera?.OnFrames(frames);

        if (!_service.Start(out string error))
        {
            _failed = true;
            Log.Info("Could not start the launch monitor: " + error);
            UpdateStatus();
            System.Windows.MessageBox.Show(
                "The launch monitor could not be started:\n\n" + error +
                "\n\nFix the settings file (tray icon > Open settings file) and choose Restart.",
                "GolfJoyBridge", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        UpdateStatus();
        if (_startupWarnings.Count > 0)
            _tray?.ShowBalloonTip(6000, "Settings problem", string.Join("\n", _startupWarnings), Forms.ToolTipIcon.Warning);
    }

    // ------------------------------------------------------------------ tray

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        _statusItem = new Forms.ToolStripMenuItem("Starting...") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var replay = new Forms.ToolStripMenuItem("Replay last shot");
        replay.Click += (_, _) => _camera?.Replay();
        menu.Items.Add(replay);

        var settings = new Forms.ToolStripMenuItem("Settings...");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var openIni = new Forms.ToolStripMenuItem("Open settings file (advanced)");
        openIni.Click += (_, _) => OpenWithShell(_iniPath);
        menu.Items.Add(openIni);

        var openLogs = new Forms.ToolStripMenuItem("Open log folder");
        openLogs.Click += (_, _) => OpenWithShell(Path.Combine(_baseDir, "logs"));
        menu.Items.Add(openLogs);

        var restart = new Forms.ToolStripMenuItem("Restart (reload settings)");
        restart.Click += (_, _) => Restart();
        menu.Items.Add(restart);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var quit = new Forms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        _tray = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "GolfJoyBridge",
        };
        SetIcon(Drawing.Color.Gray);
    }

    private void UpdateStatus()
    {
        if (_tray == null || _statusItem == null) return;

        string text;
        Drawing.Color color;
        if (_failed || _service == null)
        {
            text = "Launch monitor not started";
            color = Drawing.Color.Red;
        }
        else
        {
            text = _service.StatusText;
            color = _service.State switch
            {
                UnitState.Ready => _service.SimConnected ? Drawing.Color.LimeGreen : Drawing.Color.Gold,
                UnitState.Lost => Drawing.Color.OrangeRed,
                _ => Drawing.Color.Gray,
            };
        }

        _statusItem.Text = text;
        _tray.Text = Truncate("GolfJoyBridge - " + text, 63); // NotifyIcon tooltips are limited to 63 characters
        SetIcon(color);
    }

    /// <summary>
    /// Draws the tray icon: a small golf ball with a signal/connection badge in the corner, the badge
    /// tinted with the status colour (the ball itself never changes colour). Matches the app's icon.ico
    /// artwork at a size that still reads clearly at 16-32px.
    /// </summary>
    private void SetIcon(Drawing.Color statusColor)
    {
        if (_tray == null) return;
        const int S = 32;
        using var bmp = new Drawing.Bitmap(S, S);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            float cx = S * 0.44f, cy = S * 0.46f, r = S * 0.40f;

            // soft shadow behind the ball, so it stays visible on light backgrounds too
            using (var shadowBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(70, 0, 0, 0)))
                g.FillEllipse(shadowBrush, cx - r * 0.9f, cy - r * 0.55f, r * 2f, r * 2f);

            // the ball
            using (var ballBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 236, 235, 228)))
            using (var ballPen = new Drawing.Pen(Drawing.Color.FromArgb(255, 120, 124, 110), S * 0.045f))
            {
                g.FillEllipse(ballBrush, cx - r, cy - r, r * 2f, r * 2f);
                g.DrawEllipse(ballPen, cx - r, cy - r, r * 2f, r * 2f);
            }
            using (var highlight = new Drawing.SolidBrush(Drawing.Color.FromArgb(150, 255, 255, 255)))
            {
                float hr = r * 0.3f;
                g.FillEllipse(highlight, cx - r * 0.55f - hr, cy - r * 0.65f - hr, hr * 2f, hr * 2f);
            }

            // the badge: white separating ring, then the status-coloured circle with a signal glyph
            float bcx = S * 0.74f, bcy = S * 0.74f, brad = S * 0.30f;
            using (var ring = new Drawing.SolidBrush(Drawing.Color.White))
                g.FillEllipse(ring, bcx - brad - S * 0.04f, bcy - brad - S * 0.04f, (brad + S * 0.04f) * 2f, (brad + S * 0.04f) * 2f);
            using (var badgeBrush = new Drawing.SolidBrush(statusColor))
                g.FillEllipse(badgeBrush, bcx - brad, bcy - brad, brad * 2f, brad * 2f);

            float signalCy = bcy + brad * 0.26f;
            using (var white = new Drawing.SolidBrush(Drawing.Color.White))
            {
                float dotR = brad * 0.20f;
                g.FillEllipse(white, bcx - dotR, signalCy - dotR, dotR * 2f, dotR * 2f);
            }
            using (var arcPen = new Drawing.Pen(Drawing.Color.White, brad * 0.22f) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round })
            {
                float ar = brad * 0.62f;
                g.DrawArc(arcPen, bcx - ar, signalCy - ar, ar * 2f, ar * 2f, 210f, 120f);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        Win32.DestroyIcon(hIcon);

        var old = _currentIcon;
        _tray.Icon = icon;
        _currentIcon = icon;
        old?.Dispose();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        if (_camera == null) return;

        _settingsWindow = new SettingsWindow(_s, _iniPath, _camera, Restart);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private static void OpenWithShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info("Could not open " + path + ": " + ex.Message); }
    }

    // ------------------------------------------------------------------ lifecycle

    private void Restart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null)
                Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Info("Restart failed: " + ex.Message); return; }
        Quit();
    }

    private void Quit()
    {
        Log.Info("Quitting");
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _currentIcon?.Dispose();
        _currentIcon = null;

        try { _settingsWindow?.Close(); } catch { }
        _settingsWindow = null;

        try { _service?.Dispose(); } catch (Exception ex) { Log.Info("Shutdown error: " + ex.Message); }
        try { _camera?.Shutdown(); } catch { }
    }
}
