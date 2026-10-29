using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GolfJoyBridge.App;

/// <summary>
/// Borderless, always-on-top window that plays a shot's camera frames.
/// It can be created so it never takes keyboard focus and hides from Alt-Tab.
/// </summary>
public sealed class CameraWindow : Window
{
    private readonly System.Windows.Controls.Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _label = new()
    {
        Foreground = Brushes.Gray,
        FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly DispatcherTimer _timer = new();
    private readonly bool _noActivate;
    private readonly bool _clickThrough;

    private BitmapSource[] _frames = Array.Empty<BitmapSource>();
    private TimeSpan _frameInterval = TimeSpan.FromMilliseconds(80);
    private int _index;
    private int _loopsDone;
    private int _maxLoops;

    /// <summary>Raised when a finite playback (loops &gt; 0) has shown its last loop.</summary>
    public event Action? PlaybackFinished;

    public CameraWindow(bool noActivate, bool clickThrough, string placeholder)
    {
        _noActivate = noActivate;
        _clickThrough = clickThrough;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        ShowActivated = !noActivate;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // A fully click-through window must be a layered window; AllowsTransparency makes WPF create one.
        if (clickThrough) AllowsTransparency = true;

        // Start off-screen and tiny; the real position is applied in pixels just after Show().
        Left = -32000;
        Top = -32000;
        Width = 200;
        Height = 120;

        _label.Text = placeholder;
        var grid = new Grid();
        grid.Children.Add(_label);
        grid.Children.Add(_image);
        Content = grid;

        _timer.Tick += OnTick;
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        long bits = Win32.WS_EX_TOOLWINDOW;               // keeps it out of Alt-Tab
        if (_noActivate) bits |= Win32.WS_EX_NOACTIVATE;  // clicking it never steals focus
        if (_clickThrough) bits |= Win32.WS_EX_TRANSPARENT;
        Win32.AddExStyle(new WindowInteropHelper(this).Handle, bits);
    }

    public void ShowPlaceholder(string text)
    {
        _timer.Stop();
        _image.Source = null;
        _label.Text = text;
        _label.Visibility = Visibility.Visible;
    }

    /// <summary>Plays the frames. loops = 0 means forever.</summary>
    public void Play(BitmapSource[] frames, double fps, int loops)
    {
        _timer.Stop();
        if (frames.Length == 0) return;

        _frames = frames;
        _index = 0;
        _loopsDone = 0;
        _maxLoops = loops;
        _frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, fps));

        _label.Visibility = Visibility.Collapsed;
        _image.Source = _frames[0];
        _timer.Interval = NextInterval();
        _timer.Start();
    }

    // brief pause on the last frame so the loop's restart is easy to see
    private TimeSpan NextInterval() =>
        _index == _frames.Length - 1 ? _frameInterval + TimeSpan.FromMilliseconds(400) : _frameInterval;

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames.Length == 0) { _timer.Stop(); return; }

        _index++;
        if (_index >= _frames.Length)
        {
            _loopsDone++;
            if (_maxLoops > 0 && _loopsDone >= _maxLoops)
            {
                _timer.Stop();
                PlaybackFinished?.Invoke();
                return;
            }
            _index = 0;
        }

        _image.Source = _frames[_index];
        _timer.Interval = NextInterval();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
