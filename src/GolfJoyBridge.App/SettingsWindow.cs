using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GolfJoyBridge.App;

/// <summary>
/// The only settings UI: connection mode, wireless IP, and the camera window's mode/position/size/timing.
/// Everything else stays ini-only (edit GolfJoyBridge.ini directly).
///
/// Every control writes straight into the shared Settings object and saves the ini immediately, so
/// closing this window is enough to keep a change. Camera changes also take effect on screen right
/// away (through CameraManager). Connection changes (wired/wireless, the wireless IP) need the app to
/// restart, so they only light up the Restart button rather than reconnecting live.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly SolidColorBrush RestartIdleBrush = new(Color.FromRgb(0xE4, 0xE4, 0xE4));
    private static readonly SolidColorBrush RestartNeededBrush = new(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush RestartNeededText = Brushes.White;

    private readonly Settings _s;
    private readonly string _iniPath;
    private readonly CameraManager _camera;
    private readonly Action _restartApp;

    private bool _loading = true;
    private bool _restartNeeded;

    private TextBox _wirelessIpBox = null!;
    private Button _restartButton = null!;
    private TextBlock _restartHint = null!;
    private Panel _wirelessPanel = null!;
    private Panel _loopPanel = null!;
    private Panel _popupPanel = null!;

    public SettingsWindow(Settings settings, string iniPath, CameraManager camera, Action restartApp)
    {
        _s = settings;
        _iniPath = iniPath;
        _camera = camera;
        _restartApp = restartApp;

        Title = "GolfJoyBridge Settings";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = false;

        Content = Build();
        _loading = false;

        Loaded += (_, _) => { _camera.StartPreview(); UpdatePreview(); };
        Closed += (_, _) => _camera.StopPreview();
    }

    // ------------------------------------------------------------------ layout

    private UIElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(Header("Connection"));
        root.Children.Add(BuildConnectionSection());

        root.Children.Add(Spacer());
        root.Children.Add(Header("Shot camera"));
        root.Children.Add(BuildCameraModeSection());
        root.Children.Add(BuildMonitorSection());

        _loopPanel = BuildLoopSection();
        root.Children.Add(_loopPanel);

        _popupPanel = BuildPopupSection();
        root.Children.Add(_popupPanel);

        root.Children.Add(Spacer());
        root.Children.Add(BuildFooter());

        UpdateCameraPanelVisibility();
        return root;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        FontSize = 14,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static FrameworkElement Spacer() => new Border { Height = 1, Background = Brushes.LightGray, Margin = new Thickness(0, 14, 0, 14) };

    // ---- connection ----

    private UIElement BuildConnectionSection()
    {
        var panel = new StackPanel();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var wired = new RadioButton { Content = "Wired (always 192.168.147.147)", GroupName = "conn", Margin = new Thickness(0, 0, 16, 0) };
        var wireless = new RadioButton { Content = "Wireless", GroupName = "conn" };
        wired.IsChecked = _s.Mode == ConnectionMode.Wired;
        wireless.IsChecked = _s.Mode == ConnectionMode.Wireless;
        row.Children.Add(wired);
        row.Children.Add(wireless);
        panel.Children.Add(row);

        _wirelessPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        var ipRow = new StackPanel { Orientation = Orientation.Horizontal };
        ipRow.Children.Add(new TextBlock { Text = "Unit's Wi-Fi IP address:", VerticalAlignment = VerticalAlignment.Center, Width = 160 });
        _wirelessIpBox = new TextBox { Width = 160, Text = _s.WirelessUnitIp };
        ipRow.Children.Add(_wirelessIpBox);
        _wirelessPanel.Children.Add(ipRow);
        _wirelessPanel.Children.Add(new TextBlock
        {
            Text = "Give the unit a reserved/static address on your router so this does not change.",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_wirelessPanel);

        wired.Checked += (_, _) => { _s.Mode = ConnectionMode.Wired; AfterRestartRequiredChange(); UpdateWirelessVisibility(); };
        wireless.Checked += (_, _) => { _s.Mode = ConnectionMode.Wireless; AfterRestartRequiredChange(); UpdateWirelessVisibility(); };
        _wirelessIpBox.LostFocus += (_, _) => { _s.WirelessUnitIp = _wirelessIpBox.Text.Trim(); AfterRestartRequiredChange(); };

        UpdateWirelessVisibility();
        return panel;

        void UpdateWirelessVisibility() =>
            _wirelessPanel.Visibility = _s.Mode == ConnectionMode.Wireless ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- camera mode ----

    private UIElement BuildCameraModeSection()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var off = new RadioButton { Content = "Off", GroupName = "cammode", Margin = new Thickness(0, 0, 14, 0) };
        var loop = new RadioButton { Content = "Loop", GroupName = "cammode", Margin = new Thickness(0, 0, 14, 0) };
        var popup = new RadioButton { Content = "Popup", GroupName = "cammode" };
        off.IsChecked = _s.CameraMode == CameraMode.Off;
        loop.IsChecked = _s.CameraMode == CameraMode.Loop;
        popup.IsChecked = _s.CameraMode == CameraMode.Popup;
        row.Children.Add(off); row.Children.Add(loop); row.Children.Add(popup);

        off.Checked += (_, _) => SetCameraMode(CameraMode.Off);
        loop.Checked += (_, _) => SetCameraMode(CameraMode.Loop);
        popup.Checked += (_, _) => SetCameraMode(CameraMode.Popup);
        return row;
    }

    private void SetCameraMode(CameraMode mode)
    {
        if (_loading) return;
        _s.CameraMode = mode;
        UpdateCameraPanelVisibility();
        _camera.ApplyLiveCameraSettings();
        UpdatePreview();
        SaveNow();
    }

    private void UpdateCameraPanelVisibility()
    {
        _loopPanel.Visibility = _s.CameraMode == CameraMode.Loop ? Visibility.Visible : Visibility.Collapsed;
        _popupPanel.Visibility = _s.CameraMode == CameraMode.Popup ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- monitor (shared by loop and popup placement) ----

    private UIElement BuildMonitorSection()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock { Text = "Monitor:", VerticalAlignment = VerticalAlignment.Center, Width = 160 });
        var combo = new ComboBox { Width = 220 };
        foreach (var name in ScreenHelper.MonitorNames()) combo.Items.Add(name);
        combo.SelectedIndex = Math.Clamp(_s.Monitor, 0, combo.Items.Count - 1);
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _s.Monitor = combo.SelectedIndex;
            UpdatePreview();
            ApplyAndSave();
        };
        row.Children.Add(combo);
        return row;
    }

    // ---- loop placement ----

    private Panel BuildLoopSection()
    {
        var panel = new StackPanel();
        var screen = ScreenHelper.Pick(_s.Monitor).Bounds;
        int defX = _s.LoopX, defY = _s.LoopY;
        if (defX < 0 || defY < 0)
        {
            var (dx, dy) = ScreenHelper.DefaultLoopPosition(screen, _s.LoopWidth, _s.LoopHeight);
            if (defX < 0) defX = dx;
            if (defY < 0) defY = dy;
        }

        var xSlider = AddSlider(panel, "Position (left/right)", 0, Math.Max(100, screen.Width - 50), defX, "px",
            v => { _s.LoopX = (int)v; UpdatePreview(); ApplyAndSave(); });
        var ySlider = AddSlider(panel, "Position (up/down)", 0, Math.Max(100, screen.Height - 50), defY, "px",
            v => { _s.LoopY = (int)v; UpdatePreview(); ApplyAndSave(); });
        AddSlider(panel, "Width", 120, Math.Max(400, screen.Width), _s.LoopWidth, "px",
            v => { _s.LoopWidth = (int)v; UpdatePreview(); ApplyAndSave(); });
        AddSlider(panel, "Height", 80, Math.Max(300, screen.Height), _s.LoopHeight, "px",
            v => { _s.LoopHeight = (int)v; UpdatePreview(); ApplyAndSave(); });
        return panel;
    }

    // ---- popup placement/timing ----

    private Panel BuildPopupSection()
    {
        var panel = new StackPanel();
        var screen = ScreenHelper.Pick(_s.Monitor).Bounds;
        int defW = _s.PopupWidth > 0 ? _s.PopupWidth : screen.Width / 2;
        int defH = _s.PopupHeight > 0 ? _s.PopupHeight : defW * 9 / 16;
        int defX = _s.PopupX, defY = _s.PopupY;
        if (defX < 0 || defY < 0)
        {
            var (dx, dy) = ScreenHelper.DefaultPopupPosition(screen, defW, defH);
            if (defX < 0) defX = dx;
            if (defY < 0) defY = dy;
        }

        AddSlider(panel, "Position (left/right)", 0, Math.Max(100, screen.Width - 50), defX, "px",
            v => { _s.PopupX = (int)v; UpdatePreview(); ApplyAndSave(); });
        AddSlider(panel, "Position (up/down)", 0, Math.Max(100, screen.Height - 50), defY, "px",
            v => { _s.PopupY = (int)v; UpdatePreview(); ApplyAndSave(); });
        AddSlider(panel, "Width", 160, Math.Max(600, screen.Width), defW, "px",
            v => { _s.PopupWidth = (int)v; UpdatePreview(); ApplyAndSave(); });
        AddSlider(panel, "Height", 100, Math.Max(400, screen.Height), defH, "px",
            v => { _s.PopupHeight = (int)v; UpdatePreview(); ApplyAndSave(); });
        AddSlider(panel, "Time after shot before it shows", 0, 15, _s.PopupDelaySeconds, "s",
            v => { _s.PopupDelaySeconds = v; ApplyAndSave(); });
        AddSlider(panel, "Times it replays", 1, 10, _s.PopupRepeats, "x",
            v => { _s.PopupRepeats = (int)v; ApplyAndSave(); });
        return panel;
    }

    // ---- slider helper ----

    private Slider AddSlider(Panel into, string label, double min, double max, double value, string unit, Action<double> onChange)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = unit != "s",
            TickFrequency = 1,
        };
        var valueLabel = new TextBlock
        {
            Text = FormatSliderValue(slider.Value, unit),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueLabel, 2);
        row.Children.Add(text);
        row.Children.Add(slider);
        row.Children.Add(valueLabel);
        into.Children.Add(row);

        slider.ValueChanged += (_, e) =>
        {
            valueLabel.Text = FormatSliderValue(e.NewValue, unit);
            if (_loading) return;
            onChange(e.NewValue);
        };

        return slider;
    }

    private static string FormatSliderValue(double v, string unit) =>
        unit switch
        {
            "s" => v.ToString("0.0") + " s",
            "x" => Math.Round(v) + "x",
            _ => Math.Round(v) + " " + unit,
        };

    // ---- footer: restart button ----

    private UIElement BuildFooter()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        _restartHint = new TextBlock
        {
            Text = "",
            Foreground = Brushes.DimGray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        _restartButton = new Button
        {
            Content = "Restart app to apply",
            Padding = new Thickness(12, 6, 12, 6),
            Background = RestartIdleBrush,
        };
        _restartButton.Click += (_, _) => _restartApp();

        var close = new Button { Content = "Close", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 10, 0) };
        close.Click += (_, _) => Close();

        row.Children.Add(_restartHint);
        row.Children.Add(close);
        row.Children.Add(_restartButton);
        return row;
    }

    private void AfterRestartRequiredChange()
    {
        _restartNeeded = true;
        _restartButton.Background = RestartNeededBrush;
        _restartButton.Foreground = RestartNeededText;
        _restartHint.Text = "Connection changed - restart to apply.";
        SaveNow();
    }

    // ---- shared save/apply plumbing ----

    private void ApplyAndSave()
    {
        if (_loading) return;
        _camera.ApplyLiveCameraSettings();
        SaveNow();
    }

    private void SaveNow()
    {
        if (_loading) return;
        try { _s.Save(_iniPath); }
        catch (Exception ex) { Log.Info("Could not save settings: " + ex.Message); }
    }

    private void UpdatePreview()
    {
        bool popup = _s.CameraMode == CameraMode.Popup;
        if (popup)
        {
            int w = _s.PopupWidth > 0 ? _s.PopupWidth : ScreenHelper.Pick(_s.Monitor).Bounds.Width / 2;
            int h = _s.PopupHeight > 0 ? _s.PopupHeight : w * 9 / 16;
            _camera.UpdatePreview(_s.PopupX, _s.PopupY, w, h, centerByDefault: true);
        }
        else
        {
            _camera.UpdatePreview(_s.LoopX, _s.LoopY, _s.LoopWidth, _s.LoopHeight, centerByDefault: false);
        }
    }
}
