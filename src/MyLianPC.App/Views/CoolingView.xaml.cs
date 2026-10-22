using System.Windows;
using System.Windows.Controls;
using MyLianPC.App.Services;
using MyLianPC.Core.Cooling;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;
using MyLianPC.Lcd;

namespace MyLianPC.App.Views;

public partial class CoolingView : UserControl
{
    public sealed record Target(string Id, string Name, string Detail, bool IsPump);
    public sealed record SourceOption(string Label, TemperatureSource Source);

    private const string PumpId = "hydroshift:pump", FansId = "hydroshift:fans";
    private readonly AppState _state = AppState.Current;
    private Dictionary<string, FanCurve> _curves = [];
    private string? _selected;
    private bool _loading;
    private bool _dirty;

    public CoolingView()
    {
        InitializeComponent();
        SourceBox.ItemsSource = new[] { new SourceOption("CPU temperature", TemperatureSource.CpuPackage), new SourceOption("GPU temperature", TemperatureSource.GpuCore), new SourceOption("Coolant temperature", TemperatureSource.Coolant) };
        SourceBox.DisplayMemberPath = nameof(SourceOption.Label);
        _state.SensorsUpdated += (_, _) => RefreshLive();
        // The page is created before the profiles have arrived from the service; reload once they do (unless
        // the curve has been edited in the meantime, which must win).
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(AppState.ActiveProfile) or nameof(AppState.Profiles)) || !IsLoaded) return;
            if (_dirty) ShowPresetState(); else Load();
        };
        Loaded += (_, _) => Load();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshLive(); };
    }

    private static readonly (string Label, string Key)[] LiveReadouts =
    [
        ("CPU", Metrics.CpuTemp), ("GPU", Metrics.GpuTemp), ("Coolant", Metrics.CoolantTemp), ("Pump", Metrics.PumpRpm),
        ("Fan 1", Metrics.AioFan1), ("Fan 2", Metrics.AioFan2), ("Fan 3", Metrics.AioFan3),
    ];

    private readonly List<TextBlock> _readoutValues = [];

    private static Dictionary<string, FanCurve> Defaults() => new()
    {
        [PumpId] = CoolingPresets.Balanced(TemperatureSource.Coolant),
        [FansId] = CoolingPresets.Balanced(TemperatureSource.Coolant),
    };

    private void Load()
    {
        _curves = Defaults();
        if (_state.ActiveProfile?.Cooling is { } c) foreach (var (k, v) in c.Curves) _curves[k] = v;
        var mbFans = _state.Status?.Devices.SelectMany(d => d.Fans).Where(f => f.Id.StartsWith("mb:")).ToList() ?? [];
        var targets = new List<Target>
        {
            new(PumpId, "Pump", "HydroShift II pump head · 1600–2500 RPM", true),
            new(FansId, "Radiator fans", "HydroShift II pump head · all three fans", false),
        };
        foreach (var f in mbFans.Where(f => f.ControlEnabled))
        {
            targets.Add(new(f.Id, f.Name, $"{f.Controller} · software control enabled", false));
            _curves.TryAdd(f.Id, CoolingPresets.Balanced(TemperatureSource.CpuPackage));
        }
        var monitorOnly = mbFans.Where(f => !f.ControlEnabled).ToList();
        MonitorOnly.Text = monitorOnly.Count == 0 ? "" :
            $"{monitorOnly.Count} motherboard fan header(s) are monitored only and stay on BIOS Smart Fan control. Enable them on the Devices page to add curves.";
        Targets.ItemsSource = targets;
        // Replacing the list clears the selection first, so keep the chosen target explicitly.
        int keep = targets.FindIndex(t => t.Id == (_selected ?? targets[0].Id));
        Targets.SelectedIndex = keep >= 0 ? keep : 0;
        ApplyNote.Text = _state.ActiveProfile is { } p ? $"Saves into the active profile '{p.Name}'." : "No profile is active: a new profile 'My cooling' will be created.";
        _dirty = false;
        // Reloading the list does not necessarily raise a selection change, so refresh the editor explicitly.
        ShowCurve();
    }

    private Target? SelectedTarget => Targets.SelectedItem as Target;

    private void ShowCurve()
    {
        if (SelectedTarget is not { } t || !_curves.TryGetValue(t.Id, out var curve)) return;
        _loading = true;
        TargetTitle.Text = t.Name;
        SourceBox.SelectedIndex = curve.Source switch { TemperatureSource.CpuPackage => 0, TemperatureSource.GpuCore => 1, _ => 2 };
        bool coolant = curve.Source == TemperatureSource.Coolant;
        Editor.TemperatureLabel = "°";
        Editor.SetCurve(curve.Points, 20, coolant ? 60 : 100);
        bool isFixed = curve.Points.Count == 3 && curve.Points[0].DutyPercent == curve.Points[1].DutyPercent && curve.Points[2].DutyPercent == 100 && curve.Points[2].TemperatureC == FanCurve.CriticalTemperatureC;
        FixedBox.IsChecked = isFixed;
        FixedSlider.IsEnabled = isFixed;
        Editor.IsEditable = !isFixed;
        if (isFixed) FixedSlider.Value = curve.Points[0].DutyPercent;
        MinSlider.Value = curve.MinimumDutyPercent;
        HystSlider.Value = curve.HysteresisC;
        _loading = false;
        Validate();
        ShowPresetState();
        RefreshLive();
    }

    /// <summary>
    /// Marks the preset the editor currently shows and says whether it is already saved in the active profile, so
    /// there is no guessing about which preset is in use.
    /// </summary>
    private void ShowPresetState()
    {
        foreach (var button in new[] { PresetSilent, PresetBalanced, PresetPerformance, PresetMaximum })
            button.ClearValue(StyleProperty);

        if (SelectedTarget is not { } t || !_curves.TryGetValue(t.Id, out var curve)) { PresetState.Text = string.Empty; return; }

        var saved = _state.ActiveProfile?.Cooling is { } c && c.Curves.TryGetValue(t.Id, out var applied) ? applied : null;
        bool savedMatches = saved is not null
            && saved.Points.SequenceEqual(curve.Points)
            && saved.MinimumDutyPercent == curve.MinimumDutyPercent
            && Math.Abs(saved.HysteresisC - curve.HysteresisC) < 0.01;

        string? current = CoolingPresets.NameOf(curve);
        if (current is not null)
        {
            var button = current switch
            {
                "Silent" => PresetSilent,
                "Performance" => PresetPerformance,
                "Maximum cooling" => PresetMaximum,
                _ => PresetBalanced,
            };
            button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        }

        string what = current is null ? "a custom curve" : $"the {current} preset";
        string where = _state.ActiveProfile is { } p ? $"'{p.Name}'" : "a new profile";
        PresetState.Text = savedMatches
            ? $"In use: {what}, saved in {where}."
            : $"Showing {what} — press Apply and save to use it in {where}.";
    }

    private void Update(Func<FanCurve, FanCurve> change)
    {
        if (_loading || SelectedTarget is not { } t) return;
        _curves[t.Id] = change(_curves[t.Id]);
        _dirty = true;
        ShowCurve();
    }

    private void Validate()
    {
        var problems = _curves.SelectMany(kv => kv.Value.Validate().Select(p => $"{kv.Key}: {p}")).ToList();
        Validation.Text = string.Join("\n", problems);
        ApplyButton.IsEnabled = problems.Count == 0;
    }

    private void RefreshLive()
    {
        if (!IsVisible) return;
        var s = _state.Sensors;
        var unit = _state.Preferences.TemperatureUnit;
        // The seven readouts are built once and only their text changes; rebuilding them per sample forced a new
        // layout pass over the whole card every second.
        if (_readoutValues.Count == 0)
        {
            foreach (var (label, _) in LiveReadouts)
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption") });
                var value = new TextBlock { Style = (Style)FindResource("BigValue"), FontSize = 22 };
                panel.Children.Add(value);
                _readoutValues.Add(value);
                Readouts.Children.Add(panel);
            }
        }
        for (int i = 0; i < LiveReadouts.Length; i++)
            _readoutValues[i].Text = LcdRenderer.Format(LiveReadouts[i].Key, s[LiveReadouts[i].Key], unit);
        if (SelectedTarget is { } t && _curves.TryGetValue(t.Id, out var curve))
        {
            double? temp = curve.Source switch { TemperatureSource.CpuPackage => s[Metrics.CpuTemp], TemperatureSource.GpuCore => s[Metrics.GpuTemp], _ => s[Metrics.CoolantTemp] };
            Editor.SetCurrent(temp);
            int duty = curve.Evaluate(temp);
            SourceNow.Text = temp is null
                ? "Sensor unavailable → failsafe 100%"
                : $"now {LcdRenderer.FormatTemp(temp, unit)} → {duty}%{(t.IsPump ? $" (≈{1600 + duty * 9} RPM)" : "")}";
        }
    }

    private void Targets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedTarget is { } t) _selected = t.Id; // a cleared selection (list reload) must not forget the choice
        ShowCurve();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var src = _curves.TryGetValue(SelectedTarget?.Id ?? "", out var c) ? c.Source : TemperatureSource.Coolant;
        Update(_ => (string)((FrameworkElement)sender).Tag switch
        {
            "silent" => CoolingPresets.Silent(src),
            "performance" => CoolingPresets.Performance(src),
            "max" => CoolingPresets.Maximum(src),
            _ => CoolingPresets.Balanced(src),
        });
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceBox.SelectedItem is not SourceOption sel) return;
        // Switching between coolant (20-60 °C) and CPU/GPU (20-100 °C) needs a curve made for that range.
        Update(c => c.Source == sel.Source ? c : CoolingPresets.Balanced(sel.Source) with { MinimumDutyPercent = c.MinimumDutyPercent });
    }

    private void Editor_CurveChanged(object? sender, EventArgs e) => Update(c => c with { Points = Editor.Points.ToList() });

    private static FanCurve FixedCurve(FanCurve c, int duty) =>
        c with { Points = [new(20, duty), new(80, duty), new(FanCurve.CriticalTemperatureC, 100)], MinimumDutyPercent = Math.Min(c.MinimumDutyPercent, duty) };

    private void Fixed_Changed(object sender, RoutedEventArgs e) =>
        Update(c => FixedBox.IsChecked == true ? FixedCurve(c, (int)Math.Max(30, FixedSlider.Value)) : CoolingPresets.Balanced(c.Source));

    private void Fixed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FixedBox.IsChecked == true) Update(c => FixedCurve(c, (int)FixedSlider.Value));
    }

    private void Min_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Update(c => c with { MinimumDutyPercent = (int)MinSlider.Value, Points = c.Points.Select(p => p with { DutyPercent = Math.Max(p.DutyPercent, (int)MinSlider.Value) }).ToList() });

    private void Hyst_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Update(c => c with { HysteresisC = HystSlider.Value });

    private void Revert_Click(object sender, RoutedEventArgs e) => Load();

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var cooling = new CoolingSettings { Curves = new Dictionary<string, FanCurve>(_curves) };
        var profile = _state.ActiveProfile is { } p ? p with { Cooling = cooling } : new HardwareProfile { Id = Guid.NewGuid(), Name = "My cooling", Cooling = cooling };
        await AppState.TryAsync(c => c.SaveProfileAsync(profile), "Saving cooling curves");
        if (_state.ActiveProfile is null) await AppState.TryAsync(c => c.ActivateAsync(profile.Id), "Activating the profile");
        await _state.RefreshProfilesAsync();
        Load();
    }

    private async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "Remove custom curves from the active profile and give every motherboard fan header back to the BIOS?",
                "Restore default fan behavior", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await AppState.TryAsync(c => c.RestoreCoolingAsync(), "Restoring default cooling");
        await _state.RefreshProfilesAsync();
        await _state.RefreshStatusAsync();
        Load();
    }
}
