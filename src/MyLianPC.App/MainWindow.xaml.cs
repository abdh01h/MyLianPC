using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyLianPC.App.Services;
using MyLianPC.App.Views;
using MyLianPC.Core.Settings;

namespace MyLianPC.App;

public partial class MainWindow : Window
{
    // A class, not a record: the ListBox tracks selection by hash code, which must not change when the page is created.
    private sealed class NavItem(string glyph, string title, Func<UserControl> create)
    {
        public string Glyph { get; } = glyph;
        public string Title { get; } = title;
        public Func<UserControl> Create { get; } = create;
        public UserControl? Instance { get; set; }
    }

    private readonly List<NavItem> _items =
    [
        new("", "Dashboard", () => new DashboardView()),
        new("", "Devices", () => new DevicesView()),
        new("", "Lighting", () => new LightingView()),
        new("", "Cooler LCD", () => new LcdView()),
        new("", "Cooling", () => new CoolingView()),
        new("", "Profiles", () => new ProfilesView()),
        new("", "Diagnostics", () => new DiagnosticsView()),
        new("", "Settings", () => new SettingsView()),
    ];

    private bool _reallyClose;
    private double _zoom = 1.0;
    private const double ZoomStep = 0.1, BaseMinWidth = 980, BaseMinHeight = 640;
    private readonly System.Windows.Threading.DispatcherTimer _zoomToastTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = AppState.Current;
        Nav.ItemsSource = _items;
        Nav.SelectedIndex = 0;
        _zoomToastTimer.Tick += (_, _) => { _zoomToastTimer.Stop(); ZoomToast.Opacity = 0; };
        ApplyZoom(AppState.Current.Preferences.UiScale, showToast: false);
        IsVisibleChanged += (_, _) => UpdatePolling();
        StateChanged += (_, _) => UpdatePolling();
    }

    /// <summary>
    /// Hidden in the tray or minimized: nothing on screen consumes the readings, so the periodic sensor reads stop
    /// (the service keeps controlling the hardware) and start again as soon as the window is back.
    /// </summary>
    public void UpdatePolling()
    {
        if (IsVisible && WindowState != WindowState.Minimized) AppState.Current.ResumePolling();
        else AppState.Current.SuspendPolling();
    }

    /// <summary>
    /// Scales the whole window with Ctrl+Plus / Ctrl+Minus (and Ctrl+mouse wheel). The range is limited to what the
    /// pages are laid out for, and the minimum window size grows with the scale so nothing is squeezed out of shape.
    /// </summary>
    public void ApplyZoom(double scale, bool showToast = true)
    {
        _zoom = Math.Clamp(Math.Round(scale, 2), UserPreferences.MinUiScale, UserPreferences.MaxUiScale);
        RootLayout.LayoutTransform = Math.Abs(_zoom - 1) < 0.001 ? null : new ScaleTransform(_zoom, _zoom);
        MinWidth = BaseMinWidth * _zoom;
        MinHeight = BaseMinHeight * _zoom;
        if (showToast) ShowZoomToast();
        var prefs = AppState.Current.Preferences;
        if (Math.Abs(prefs.UiScale - _zoom) > 0.001) AppState.Current.SavePreferences(prefs with { UiScale = _zoom });
    }

    private void ZoomBy(double delta) => ApplyZoom(_zoom + delta);

    private void ShowZoomToast()
    {
        ZoomToastText.Text = $"Interface {_zoom * 100:0}%";
        ZoomToast.Opacity = 1;
        _zoomToastTimer.Stop();
        _zoomToastTimer.Start();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.OemPlus:
            case System.Windows.Input.Key.Add:
                ZoomBy(ZoomStep);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.OemMinus:
            case System.Windows.Input.Key.Subtract:
                ZoomBy(-ZoomStep);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D0:
            case System.Windows.Input.Key.NumPad0:
                ApplyZoom(1.0);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPreviewMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        ZoomBy(e.Delta > 0 ? ZoomStep : -ZoomStep);
        e.Handled = true;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not NavItem item) return;
        item.Instance ??= item.Create();
        Page.Content = item.Instance;
    }

    public void Navigate(string title)
    {
        var i = _items.FindIndex(x => x.Title == title);
        if (i >= 0) Nav.SelectedIndex = i;
    }

    public void SaveScreenshot(string file)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var content = (FrameworkElement)Content;

        // A screenshot is a fixed reference image: capture it at 100% (the interface zoom is a layout transform on
        // the root Grid and would otherwise be applied twice) and with a window big enough to show the whole page
        // (the Cooler LCD page is taller than the default window).
        var zoom = RootLayout.LayoutTransform;
        RootLayout.LayoutTransform = null;
        Width = Math.Max(Width, 1480);
        Height = Math.Max(Height, 1000);
        UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Shapes.Rectangle { Width = content.ActualWidth, Height = content.ActualHeight, Fill = Background ?? System.Windows.Media.Brushes.Black };
        bg.Measure(new Size(content.ActualWidth, content.ActualHeight));
        bg.Arrange(new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        rtb.Render(bg);
        rtb.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);

        RootLayout.LayoutTransform = zoom;
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window never stops hardware control: the service keeps running.
        if (!_reallyClose && AppState.Current.Preferences.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        if (!_reallyClose) ((App)Application.Current).ExitApp();
    }
}
