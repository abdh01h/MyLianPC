using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyLianPC.App.Controls;
using MyLianPC.App.Services;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;

namespace MyLianPC.App.Views;

public partial class LightingView : UserControl
{
    public sealed record ZoneItem(ZoneInfo Zone, Brush Swatch, string Detail);
    public sealed record Chip(int Index, Brush Brush, Thickness Border, string Hex);

    private readonly AppState _state = AppState.Current;
    private List<ZoneInfo> _zones = [];
    private ZoneLighting _sync = new();
    private Dictionary<string, ZoneLighting> _perZone = [];
    private string? _selectedZone;
    private int _selectedColor;
    private bool _loading;

    public LightingView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromProfile();
    }

    private ZoneLighting Current => SyncBox.IsChecked == true || _selectedZone is null ? _sync : _perZone.GetValueOrDefault(_selectedZone, _sync);

    private void SetCurrent(ZoneLighting z)
    {
        if (SyncBox.IsChecked == true || _selectedZone is null) _sync = z;
        else _perZone[_selectedZone] = z;
    }

    private void LoadFromProfile()
    {
        _loading = true;
        _zones = _state.Status?.Devices.SelectMany(d => d.Zones).ToList() ?? [];
        var lighting = _state.ActiveProfile?.Lighting;
        _sync = lighting?.SyncAll ?? lighting?.Zones.Values.FirstOrDefault() ?? new ZoneLighting { Effect = LightingEffect.Static, Colors = [RgbColor.ParseHex("#00FFAA")] };
        _perZone = _zones.ToDictionary(z => z.Id, z => lighting?.For(z.Id) ?? _sync);
        SyncBox.IsChecked = lighting is null || lighting.SyncAll is not null;
        _selectedZone = _zones.FirstOrDefault()?.Id;
        SaveTarget.Text = _state.ActiveProfile is { } p ? $"Saves into the active profile '{p.Name}'." : "No profile is active: a new profile 'My lighting' will be created.";
        _loading = false;
        RefreshAll();
    }

    private void RefreshAll()
    {
        _loading = true;
        bool sync = SyncBox.IsChecked == true;
        ZoneList.IsEnabled = !sync;
        ZoneList.ItemsSource = _zones.Select(z =>
        {
            var l = sync ? _sync : _perZone.GetValueOrDefault(z.Id, _sync);
            var c = l.Effect == LightingEffect.Off ? RgbColor.Black : l.Colors.FirstOrDefault(RgbColor.White);
            var dev = _state.Status?.Devices.FirstOrDefault(d => d.Id == z.DeviceId);
            return new ZoneItem(z, new SolidColorBrush(c.ToMedia()), $"{dev?.Manufacturer} · {(z.LedCount > 1 ? $"{z.LedCount} LEDs · " : "")}{l.Effect}{(dev?.State == DeviceState.Connected ? "" : $" · {dev?.State}")}");
        }).ToList();
        if (!sync) ZoneList.SelectedItem = ((List<ZoneItem>)ZoneList.ItemsSource).FirstOrDefault(i => i.Zone.Id == _selectedZone);

        var targetZones = sync ? _zones : _zones.Where(z => z.Id == _selectedZone).ToList();
        var effects = targetZones.SelectMany(z => z.Effects).Distinct().OrderBy(e => e).ToList();
        if (effects.Count == 0) effects = [LightingEffect.Off, LightingEffect.Static];
        EffectBox.ItemsSource = effects;
        var cur = Current;
        EffectBox.SelectedItem = effects.Contains(cur.Effect) ? cur.Effect : effects.FirstOrDefault(e => e == LightingEffect.Static);
        var unsupported = targetZones.Where(z => !z.Effects.Contains(cur.Effect)).Select(z => z.Name).ToList();
        EffectNote.Text = unsupported.Count == 0 ? "" : $"Not supported on {string.Join(", ", unsupported)}: the closest supported effect is used there.";
        EditorTitle.Text = sync ? "All devices" : _zones.FirstOrDefault(z => z.Id == _selectedZone)?.Name ?? "Zone";

        // Sync-all edits every zone at once: allow as many colors as the most capable zone takes; each device
        // trims the list to what it supports when the profile is applied.
        int maxColors = Math.Max(1, targetZones.Select(z => z.MaxColors).DefaultIfEmpty(1).Max());
        bool usesColors = cur.Effect is not (LightingEffect.Off or LightingEffect.Rainbow or LightingEffect.RainbowWave);
        ColorSection.IsEnabled = usesColors;
        _selectedColor = Math.Clamp(_selectedColor, 0, Math.Max(0, cur.Colors.Count - 1));
        ColorChips.ItemsSource = cur.Colors.Select((c, i) => new Chip(i, new SolidColorBrush(c.ToMedia()), new Thickness(i == _selectedColor ? 3 : 1), c.ToHex())).ToList();
        bool multi = cur.Effect is LightingEffect.Gradient or LightingEffect.Wave or LightingEffect.ColorCycle or LightingEffect.Breathing or LightingEffect.Flash;
        AddColorButton.IsEnabled = multi && cur.Colors.Count < Math.Min(6, maxColors);
        RemoveColorButton.IsEnabled = cur.Colors.Count > 1;
        if (cur.Colors.Count > 0) Picker.SetColor(cur.Colors[_selectedColor]);
        BrightnessSlider.Value = cur.Brightness;
        SpeedSlider.Value = cur.Speed;
        SpeedSlider.IsEnabled = cur.Effect is not (LightingEffect.Off or LightingEffect.Static or LightingEffect.Gradient);
        Preview.LedCount = targetZones.Select(z => z.LedCount).Where(n => n > 1).DefaultIfEmpty(24).Max() is var n && n > 40 ? 40 : n;
        Preview.SetLighting(cur);
        _loading = false;
    }

    private void Changed(ZoneLighting z)
    {
        SetCurrent(z);
        RefreshAll();
    }

    private void Sync_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (SyncBox.IsChecked == false) foreach (var z in _zones) _perZone[z.Id] = _sync;
        RefreshAll();
    }

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ZoneList.SelectedItem is not ZoneItem item) return;
        _selectedZone = item.Zone.Id;
        _selectedColor = 0;
        RefreshAll();
    }

    private void Effect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EffectBox.SelectedItem is not LightingEffect fx) return;
        var cur = Current;
        var colors = cur.Colors.ToList();
        if (colors.Count == 0) colors.Add(RgbColor.ParseHex("#00FFAA"));
        if (fx is LightingEffect.Gradient or LightingEffect.Wave && colors.Count < 2) colors.Add(RgbColor.ParseHex("#7A00FF"));
        if (fx is LightingEffect.Static or LightingEffect.Off) colors = [colors[0]];
        Changed(cur with { Effect = fx, Colors = colors });
    }

    private void Picker_ColorChanged(object? sender, RgbColor c)
    {
        if (_loading) return;
        var colors = Current.Colors.ToList();
        if (colors.Count == 0) colors.Add(c);
        colors[Math.Clamp(_selectedColor, 0, colors.Count - 1)] = c;
        Changed(Current with { Colors = colors });
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        _selectedColor = (int)((FrameworkElement)sender).Tag;
        RefreshAll();
    }

    private void AddColor_Click(object sender, RoutedEventArgs e)
    {
        var colors = Current.Colors.Append(RgbColor.FromHsv(Current.Colors.Count * 70 + 200)).ToList();
        _selectedColor = colors.Count - 1;
        Changed(Current with { Colors = colors });
    }

    private void RemoveColor_Click(object sender, RoutedEventArgs e)
    {
        var colors = Current.Colors.ToList();
        if (colors.Count <= 1) return;
        colors.RemoveAt(_selectedColor);
        _selectedColor = 0;
        Changed(Current with { Colors = colors });
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        Changed(Current with { Brightness = (int)BrightnessSlider.Value, Speed = (int)SpeedSlider.Value });
    }

    private void Off_Click(object sender, RoutedEventArgs e)
    {
        SyncBox.IsChecked = true;
        Changed(_sync with { Effect = LightingEffect.Off });
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => LoadFromProfile();

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var lighting = SyncBox.IsChecked == true
            ? new LightingSettings { SyncAll = _sync }
            : new LightingSettings { Zones = new Dictionary<string, ZoneLighting>(_perZone) };
        var profile = _state.ActiveProfile is { } p
            ? p with { Lighting = lighting }
            : new HardwareProfile { Id = Guid.NewGuid(), Name = "My lighting", Lighting = lighting };
        var saved = await AppState.TryAsync(c => c.SaveProfileAsync(profile), "Saving lighting");
        if (saved.ValueKind == System.Text.Json.JsonValueKind.Undefined) return;
        if (_state.ActiveProfile is null)
        {
            var report = await AppState.TryAsync(c => c.ActivateAsync(profile.Id), "Activating the profile");
            if (report is not null) ApplyReportDialog.Show(report, onlyOnFailure: true);
        }
        else if (Core.Ipc.IpcJson.From<ApplyReport>(saved) is { Results.Count: > 0 } report) ApplyReportDialog.Show(report, onlyOnFailure: true);
        await _state.RefreshProfilesAsync();
        await _state.RefreshStatusAsync();
    }
}
