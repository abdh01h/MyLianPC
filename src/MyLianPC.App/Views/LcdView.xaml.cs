using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using MyLianPC.App.Controls;
using MyLianPC.App.Services;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;
using MyLianPC.Lcd;

namespace MyLianPC.App.Views;

public partial class LcdView : UserControl
{
    public sealed record ThemeItem(LcdTheme Theme, BitmapSource Thumb);

    public sealed partial class WidgetVm : ObservableObject
    {
        public static IReadOnlyList<LcdWidgetType> AllTypes { get; } = Enum.GetValues<LcdWidgetType>();
        public IReadOnlyList<LcdWidgetType> Types => AllTypes;
        public IReadOnlyList<Metrics.Info> MetricChoices => Metrics.All;
        [ObservableProperty] private LcdWidgetType _type;
        [ObservableProperty] private string _metric = Metrics.CpuTemp;
        [ObservableProperty] private string _caption = "";
        [ObservableProperty] private float _size = 48;
        public float X { get; set; } = 240;
        public float Y { get; set; } = 240;
        public RgbColor? Color { get; set; }

        public LcdWidget ToWidget() => new()
        {
            Type = Type, Metric = Metric, X = X, Y = Y, Size = Math.Clamp(Size, 8, 300), Color = Color,
            Label = Type == LcdWidgetType.Text ? null : Caption, Text = Type == LcdWidgetType.Text ? Caption : null,
        };

        public static WidgetVm From(LcdWidget w) => new()
        {
            Type = w.Type, Metric = w.Metric, Size = w.Size, X = w.X, Y = w.Y, Color = w.Color,
            Caption = (w.Type == LcdWidgetType.Text ? w.Text : w.Label) ?? "",
        };
    }

    private const double PreviewScale = 320.0 / LcdRenderer.Size;
    private readonly AppState _state = AppState.Current;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LcdSettings _s = new();
    private List<WidgetVm> _widgets = [];
    private LcdMedia? _media;
    private int _mediaFrame;
    private string? _editingColor;
    private bool _loading;

    public LcdView()
    {
        InitializeComponent();
        UnitBox.ItemsSource = Enum.GetValues<TemperatureUnit>();
        GaugeBox.ItemsSource = Enum.GetValues<GaugeStyle>();
        RotationBox.ItemsSource = new[] { 0, 90, 180, 270 };
        RotationBox.ItemStringFormat = "{0}°";
        RefreshBox.ItemsSource = new[] { 500, 1000, 2000, 5000 };
        RefreshBox.ItemStringFormat = "every {0} ms";
        TimeBox.ItemsSource = new[] { "24-hour (17:04)", "12-hour (5:04 PM)" };
        FontBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToList();
        for (int i = 0; i < 6; i++)
        {
            var cb = new ComboBox { ItemsSource = Metrics.All, DisplayMemberPath = "Label", SelectedValuePath = "Key", Margin = new Thickness(0, 0, 8, 6), Tag = i };
            cb.SelectionChanged += Option_Changed;
            MetricSlots.Children.Add(cb);
        }
        _previewTimer.Tick += (_, _) => RenderPreview();
        IsVisibleChanged += (_, _) => { if (IsVisible) _previewTimer.Start(); else _previewTimer.Stop(); };
        Loaded += (_, _) => { Load(_state.ActiveProfile?.Lcd ?? new LcdSettings()); BuildThumbnails(); };
    }

    private void Load(LcdSettings s)
    {
        _loading = true;
        _s = s;
        (s.Mode switch { LcdMode.Image => ModeImage, LcdMode.Animation => ModeAnimation, LcdMode.Off => ModeOff, _ => ModeTheme }).IsChecked = true;
        FontBox.SelectedItem = s.FontFamily;
        FontScale.Value = s.FontScale;
        UnitBox.SelectedItem = s.Unit;
        GaugeBox.SelectedItem = s.GaugeStyle;
        LcdBrightness.Value = s.Brightness;
        RotationBox.SelectedItem = s.RotationDegrees;
        RefreshBox.SelectedItem = new[] { 500, 1000, 2000, 5000 }.OrderBy(v => Math.Abs(v - s.RefreshMilliseconds)).First();
        TimeBox.SelectedIndex = s.Clock24Hour ? 0 : 1;
        OverlayBox.IsChecked = s.MediaOverlay;
        for (int i = 0; i < 6; i++) ((ComboBox)MetricSlots.Children[i]).SelectedValue = s.Metrics.ElementAtOrDefault(i);
        _widgets = s.Widgets.Select(WidgetVm.From).ToList();
        foreach (var w in _widgets) w.PropertyChanged += Widget_Changed;
        WidgetList.ItemsSource = _widgets;
        LoadMedia(s.MediaFile);
        _loading = false;
        UpdateVisibility();
        RenderPreview();
    }

    private void LoadMedia(string? file)
    {
        _media?.Dispose();
        _media = null;
        MediaName.Text = file is null ? "No file chosen" : file;
        if (file is null) return;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyLianPC", "media", file);
            if (File.Exists(path)) _media = LcdMedia.Load(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { MediaName.Text = $"{file} (cannot preview: {e.Message})"; }
    }

    /// <summary>Builds the settings from the controls (the single source of truth for Apply and the preview).</summary>
    private LcdSettings Collect()
    {
        var mode = ModeImage.IsChecked == true ? LcdMode.Image : ModeAnimation.IsChecked == true ? LcdMode.Animation : ModeOff.IsChecked == true ? LcdMode.Off : LcdMode.Theme;
        var metrics = MetricSlots.Children.OfType<ComboBox>().Select(c => c.SelectedValue as string).Where(k => k is not null).Cast<string>().ToList();
        return _s with
        {
            Mode = mode,
            ThemeId = (ThemeList.SelectedItem as ThemeItem)?.Theme.Id ?? _s.ThemeId,
            FontFamily = FontBox.SelectedItem as string ?? _s.FontFamily,
            FontScale = Math.Round(FontScale.Value, 2),
            Unit = UnitBox.SelectedItem is TemperatureUnit u ? u : _s.Unit,
            GaugeStyle = GaugeBox.SelectedItem is GaugeStyle g ? g : _s.GaugeStyle,
            Brightness = (int)LcdBrightness.Value,
            RotationDegrees = RotationBox.SelectedItem is int r ? r : 0,
            RefreshMilliseconds = RefreshBox.SelectedItem is int ms ? ms : 1000,
            Clock24Hour = TimeBox.SelectedIndex != 1,
            MediaOverlay = OverlayBox.IsChecked == true,
            Metrics = metrics.Count > 0 ? metrics : _s.Metrics,
            Widgets = _widgets.Select(w => w.ToWidget()).ToList(),
        };
    }

    private void UpdateVisibility()
    {
        bool media = ModeImage.IsChecked == true || ModeAnimation.IsChecked == true;
        bool theme = ModeTheme.IsChecked == true;
        ThemeCard.Visibility = theme ? Visibility.Visible : Visibility.Collapsed;
        MediaCard.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        StyleCard.Visibility = theme || media ? Visibility.Visible : Visibility.Collapsed;
        WidgetCard.Visibility = theme && (ThemeList.SelectedItem as ThemeItem)?.Theme.Id == "custom" ? Visibility.Visible : Visibility.Collapsed;
        var hs = _state.Status?.Devices.FirstOrDefault(d => d.HasLcd);
        LcdState.Text = hs is null ? "Cooler status unknown." : $"Cooler: {hs.State}{(hs.Error is null ? "" : " — " + hs.Error)}";
    }

    private void BuildThumbnails()
    {
        var style = Collect();
        var items = LcdRenderer.Themes.Where(t => t.Id != "media").Select(t =>
        {
            using var bmp = LcdRenderer.Render(style with { Mode = LcdMode.Theme, ThemeId = t.Id }, LcdMetrics.Sample);
            using var small = bmp.Resize(new SkiaSharp.SKImageInfo(110, 110), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear));
            return new ThemeItem(t, SkiaInterop.ToBitmapSource(small));
        }).ToList();
        _loading = true;
        ThemeList.ItemsSource = items;
        ThemeList.SelectedItem = items.FirstOrDefault(i => i.Theme.Id == _s.ThemeId) ?? items[0];
        _loading = false;
        UpdateVisibility();
    }

    /// <summary>
    /// Lists inside the scrolling column swallow the wheel even when they cannot scroll themselves; forward it to
    /// the page's ScrollViewer so the wheel always scrolls the page.
    /// </summary>
    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        for (DependencyObject? p = VisualTreeHelper.GetParent((DependencyObject)sender); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is not ScrollViewer sv) continue;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }
    }

    private void RenderPreview()
    {
        if (!IsLoaded) return;
        var s = Collect();
        SkiaSharp.SKBitmap? frame = _media is { Frames.Count: > 0 } m ? m.Frames[_mediaFrame++ % m.Frames.Count] : null;
        using var bmp = LcdRenderer.Render(s, new LcdMetrics(_state.Sensors.Values, DateTime.Now), frame);
        PreviewImage.Source = SkiaInterop.ToBitmapSource(bmp);
        PreviewImage.RenderTransform = new RotateTransform(s.RotationDegrees, 160, 160);
        PreviewImage.Opacity = s.Mode == LcdMode.Off ? 0 : 0.25 + 0.75 * s.Brightness / 100.0;
        _previewTimer.Interval = TimeSpan.FromMilliseconds(_media is { IsAnimated: true } && s.Mode != LcdMode.Theme ? Math.Max(66, _media.DurationsMs[0]) : 1000);
        DrawHandles(s);
    }

    private void DrawHandles(LcdSettings s)
    {
        WidgetHandles.Children.Clear();
        if (s.Mode != LcdMode.Theme || s.ThemeId != "custom") return;
        foreach (var w in _widgets)
        {
            var thumb = new Thumb { Width = 16, Height = 16, Cursor = System.Windows.Input.Cursors.SizeAll, ToolTip = $"{w.Type} {w.Caption}", Opacity = 0.85 };
            Canvas.SetLeft(thumb, w.X * PreviewScale - 8);
            Canvas.SetTop(thumb, w.Y * PreviewScale - 8);
            thumb.DragDelta += (_, e) =>
            {
                w.X = (float)Math.Clamp(w.X + e.HorizontalChange / PreviewScale, 0, LcdRenderer.Size);
                w.Y = (float)Math.Clamp(w.Y + e.VerticalChange / PreviewScale, 0, LcdRenderer.Size);
                Canvas.SetLeft(thumb, w.X * PreviewScale - 8);
                Canvas.SetTop(thumb, w.Y * PreviewScale - 8);
            };
            thumb.DragCompleted += (_, _) => RenderPreview();
            WidgetHandles.Children.Add(thumb);
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateVisibility();
        RenderPreview();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateVisibility();
        RenderPreview();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        RenderPreview();
    }

    private void Widget_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loading) RenderPreview();
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        _editingColor = (string)((FrameworkElement)sender).Tag;
        PopupPicker.SetColor(_editingColor switch { "bg" => _s.Background, "text" => _s.TextColor, "accent" => _s.Accent, _ => _s.Secondary });
        ColorPopup.IsOpen = true;
    }

    private void PopupPicker_ColorChanged(object? sender, RgbColor c)
    {
        _s = _editingColor switch
        {
            "bg" => _s with { Background = c },
            "text" => _s with { TextColor = c },
            "accent" => _s with { Accent = c },
            _ => _s with { Secondary = c },
        };
        RenderPreview();
    }

    private async void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images and animations|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var bytes = await File.ReadAllBytesAsync(dlg.FileName);
        var stored = await AppState.TryAsync(c => c.UploadMediaAsync(Path.GetFileName(dlg.FileName), bytes), "Uploading the file");
        if (stored is null) return;
        _s = _s with { MediaFile = stored };
        LoadMedia(stored);
        if (_media is { IsAnimated: true }) ModeAnimation.IsChecked = true;
        else ModeImage.IsChecked = true;
        RenderPreview();
    }

    private void AddWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_widgets.Count >= LcdSettings.MaxWidgets) return;
        var w = new WidgetVm { Type = LcdWidgetType.Metric, Metric = Metrics.GpuTemp, Caption = "GPU", Size = 40 };
        w.PropertyChanged += Widget_Changed;
        _widgets = [.. _widgets, w];
        WidgetList.ItemsSource = _widgets;
        RenderPreview();
    }

    private void RemoveWidget_Click(object sender, RoutedEventArgs e)
    {
        _widgets = _widgets.Where(w => w != ((FrameworkElement)sender).Tag).ToList();
        WidgetList.ItemsSource = _widgets;
        RenderPreview();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        Load(_state.ActiveProfile?.Lcd ?? new LcdSettings());
        BuildThumbnails();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var lcd = Collect();
        var problems = lcd.Validate();
        if (problems.Count > 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, string.Join("\n", problems), "LCD settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var profile = _state.ActiveProfile is { } p ? p with { Lcd = lcd } : new HardwareProfile { Id = Guid.NewGuid(), Name = "My LCD", Lcd = lcd };
        await AppState.TryAsync(c => c.SaveProfileAsync(profile), "Saving the LCD layout");
        if (_state.ActiveProfile is null) await AppState.TryAsync(c => c.ActivateAsync(profile.Id), "Activating the profile");
        _s = lcd;
        await _state.RefreshProfilesAsync();
        await _state.RefreshStatusAsync();
        UpdateVisibility();
    }
}
