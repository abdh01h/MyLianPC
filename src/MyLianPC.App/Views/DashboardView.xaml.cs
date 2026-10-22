using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MyLianPC.App.Controls;
using MyLianPC.App.Services;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;
using MyLianPC.Lcd;

namespace MyLianPC.App.Views;

public partial class DashboardView : UserControl
{
    /// <summary>One reading inside a component card. <paramref name="Hint"/> is the full name behind a short label.</summary>
    public readonly record struct RowData(string Label, string Value, string? Detail = null, string? Hint = null);

    /// <summary>
    /// A reading row that updates in place. The dashboard keeps its visual tree between samples and only the text
    /// that actually changed is re-rendered, instead of throwing away and rebuilding every card once a second.
    /// </summary>
    public sealed partial class RowVm : ObservableObject
    {
        [ObservableProperty] private string _label = "";
        [ObservableProperty] private string _value = "";
        [ObservableProperty] private string? _detail;
        [ObservableProperty] private string _tooltip = "";

        public void Set(in RowData row)
        {
            Label = row.Label;
            Value = row.Value;
            Detail = row.Detail;
            Tooltip = row.Hint ?? row.Label;
        }
    }

    /// <summary>One component (CPU, GPU, Memory, Storage, Cooler) with all of its selected readings in a single box.</summary>
    public sealed partial class CardVm : ObservableObject
    {
        public required string Group { get; init; }
        public required Brush Accent { get; init; }
        [ObservableProperty] private string? _spec;
        public ObservableCollection<RowVm> Rows { get; } = [];
    }

    /// <summary>A chart legend entry whose dot carries the series colour; only its text follows the readings.</summary>
    public sealed partial class LegendVm : ObservableObject
    {
        public required Brush Brush { get; init; }
        [ObservableProperty] private string _text = "";
    }

    /// <summary>All readings of one component in the customize list, with a checkbox that selects them together.</summary>
    public sealed partial class MetricGroup : ObservableObject
    {
        public required string Name { get; init; }
        public required IReadOnlyList<MetricChoice> Items { get; init; }
        [ObservableProperty] private bool _allSelected;
        private bool _syncing;

        partial void OnAllSelectedChanged(bool value)
        {
            if (_syncing) return;
            _syncing = true;
            foreach (var item in Items) item.Selected = value;
            _syncing = false;
        }

        /// <summary>Called when one of the rows is ticked or unticked.</summary>
        public void RefreshFromItems()
        {
            _syncing = true;
            AllSelected = Items.Count > 0 && Items.All(i => i.Selected);
            _syncing = false;
        }
    }

    private static readonly IReadOnlyDictionary<string, Color> GroupColors = new Dictionary<string, Color>
    {
        ["CPU"] = Color.FromRgb(0x00, 0xC2, 0xA8),
        ["GPU"] = Color.FromRgb(0x7A, 0x5C, 0xFF),
        ["Memory"] = Color.FromRgb(0x40, 0xB0, 0x60),
        ["Cooler"] = Color.FromRgb(0x2D, 0x8C, 0xFF),
        ["Storage"] = Color.FromRgb(0xFF, 0xB0, 0x20),
    };

    /// <summary>Frozen brushes for the card accents and the legend dots, created once instead of per refresh.</summary>
    private static readonly IReadOnlyDictionary<string, SolidColorBrush> GroupBrushes =
        GroupColors.ToDictionary(kv => kv.Key, kv => Freeze(new SolidColorBrush(kv.Value)));

    private static readonly string[] GroupOrder = ["CPU", "GPU", "Memory", "Storage", "Cooler"];

    /// <summary>Empty series for a metric that has no samples yet.</summary>
    private static readonly MetricHistory NoHistory = new(1);

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public sealed partial class MetricChoice : ObservableObject
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Group { get; init; }
        [ObservableProperty] private bool _selected;
    }

    private readonly AppState _state = AppState.Current;
    private List<MetricGroup> _metricGroups = [];
    private readonly ObservableCollection<CardVm> _cardSource = [];
    private List<CardVm> _cards = [];
    private readonly LegendVm _tempCpu, _tempGpu, _tempCoolant, _loadCpu, _loadGpu, _loadMemory;

    public DashboardView()
    {
        InitializeComponent();
        DataContext = _state;
        _state.SensorsUpdated += (_, _) => Refresh();
        _state.PropertyChanged += State_PropertyChanged;
        Loaded += (_, _) => { RefreshProfiles(); Refresh(); };
        // The cards and legends are created once and then only their text changes, so the page is not rebuilt per sample.
        Cards.ItemsSource = _cardSource;
        _tempCpu = new LegendVm { Brush = GroupBrushes["CPU"] };
        _tempGpu = new LegendVm { Brush = GroupBrushes["GPU"] };
        _tempCoolant = new LegendVm { Brush = GroupBrushes["Cooler"] };
        _loadCpu = new LegendVm { Brush = GroupBrushes["CPU"] };
        _loadGpu = new LegendVm { Brush = GroupBrushes["GPU"] };
        _loadMemory = new LegendVm { Brush = GroupBrushes["Memory"] };
        TempLegend.ItemsSource = new[] { _tempCpu, _tempGpu, _tempCoolant };
        LoadLegend.ItemsSource = new[] { _loadCpu, _loadGpu, _loadMemory };
        // Hidden pages (another tab is open, or the window is in the tray) do not need to be redrawn.
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Profiles) or nameof(AppState.Status)) RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        var list = _state.Profiles.Profiles.OrderBy(p => p.Name).ToList();
        var active = list.FirstOrDefault(p => p.Id == _state.Profiles.ActiveProfileId);
        if (!ReferenceEquals(ProfileBox.ItemsSource, list) && (ProfileBox.ItemsSource is not IEnumerable<HardwareProfile> old || !old.Select(p => p.Id).SequenceEqual(list.Select(p => p.Id))))
            ProfileBox.ItemsSource = list;
        if (!ProfileBox.IsDropDownOpen) ProfileBox.SelectedItem = ((IEnumerable<HardwareProfile>)ProfileBox.ItemsSource).FirstOrDefault(p => p.Id == active?.Id);
        if (_state.Status is { } st)
        {
            int ok = st.Devices.Count(d => d.State == DeviceState.Connected);
            DeviceSummary.Text = $"{ok} of {st.Devices.Count} devices connected · service {st.Version}";
        }
    }

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not HardwareProfile p) return;
        var report = await AppState.TryAsync(c => c.ActivateAsync(p.Id), "Applying the profile");
        await _state.RefreshProfilesAsync();
        await _state.RefreshStatusAsync();
        if (report is not null) ApplyReportDialog.Show(report);
    }

    private void Refresh()
    {
        if (!IsVisible) return;
        var s = _state.Sensors;
        var unit = _state.Preferences.TemperatureUnit;
        void G(Gauge g, string key)
        {
            g.Value = s[key];
            g.Text = LcdRenderer.Format(key, s[key], unit);
        }
        G(GCpu, Metrics.CpuTemp);
        G(GGpu, Metrics.GpuTemp);
        G(GCoolant, Metrics.CoolantTemp);
        G(GCpuLoad, Metrics.CpuLoad);
        G(GGpuLoad, Metrics.GpuLoad);
        G(GRam, Metrics.RamLoad);

        UpdateCards(s, unit);

        // Fahrenheit is applied while drawing (scale/offset) so no converted copy of the history is made per sample.
        bool fahrenheit = unit == TemperatureUnit.Fahrenheit;
        double scale = fahrenheit ? 9.0 / 5 : 1, offset = fahrenheit ? 32 : 0;
        TempChart.SetSeries(
        [
            new("CPU", GroupColors["CPU"], History(Metrics.CpuTemp), scale, offset),
            new("GPU", GroupColors["GPU"], History(Metrics.GpuTemp), scale, offset),
            new("Coolant", GroupColors["Cooler"], History(Metrics.CoolantTemp), scale, offset),
        ]);
        _tempCpu.Text = $"CPU {LcdRenderer.FormatTemp(s[Metrics.CpuTemp], unit)}";
        _tempGpu.Text = $"GPU {LcdRenderer.FormatTemp(s[Metrics.GpuTemp], unit)}";
        _tempCoolant.Text = $"Coolant {LcdRenderer.FormatTemp(s[Metrics.CoolantTemp], unit)}";
        LoadChart.SetSeries(
        [
            new("CPU", GroupColors["CPU"], History(Metrics.CpuLoad)),
            new("GPU", GroupColors["GPU"], History(Metrics.GpuLoad)),
            new("Memory", GroupColors["Memory"], History(Metrics.RamLoad)),
        ]);
        _loadCpu.Text = $"CPU {LcdRenderer.Format(Metrics.CpuLoad, s[Metrics.CpuLoad], unit)}";
        _loadGpu.Text = $"GPU {LcdRenderer.Format(Metrics.GpuLoad, s[Metrics.GpuLoad], unit)}";
        _loadMemory.Text = $"Memory {LcdRenderer.Format(Metrics.RamLoad, s[Metrics.RamLoad], unit)}";
    }

    private MetricHistory History(string key) => _state.History.TryGetValue(key, out var h) ? h : NoHistory;

    /// <summary>
    /// Syncs the cards with the new sample: the card list is only rebuilt when the chosen metrics changed (which
    /// changes which cards exist), otherwise every card and row keeps its objects and just gets new text.
    /// </summary>
    private void UpdateCards(SensorSnapshot s, TemperatureUnit unit)
    {
        var selected = _state.Preferences.DashboardMetrics.ToHashSet();
        var cards = new List<(string Group, string? Spec, List<RowData> Rows)>();
        foreach (var group in GroupOrder)
        {
            var rows = group == "Storage"
                ? StorageRows(s, selected, unit)
                : Metrics.All
                    .Where(i => i.Group == group && selected.Contains(i.Key))
                    .Select(i => new RowData(ShortLabel(i), LcdRenderer.Format(i.Key, s[i.Key], unit)))
                    .ToList();
            if (rows.Count == 0) continue;
            cards.Add((group, Spec(s, group), rows));
        }

        if (_cards.Count != cards.Count || !_cards.Select(c => c.Group).SequenceEqual(cards.Select(c => c.Group)))
        {
            _cards = [.. cards.Select(c => new CardVm { Group = c.Group, Accent = GroupBrushes.TryGetValue(c.Group, out var b) ? b : Brushes.Gray })];
            _cardSource.Clear();
            foreach (var card in _cards) _cardSource.Add(card);
        }
        for (int i = 0; i < cards.Count; i++)
        {
            var card = _cards[i];
            card.Spec = cards[i].Spec;
            var rows = cards[i].Rows;
            while (card.Rows.Count > rows.Count) card.Rows.RemoveAt(card.Rows.Count - 1);
            while (card.Rows.Count < rows.Count) card.Rows.Add(new RowVm());
            for (int r = 0; r < rows.Count; r++) card.Rows[r].Set(rows[r]);
        }
    }

    private static string? Spec(SensorSnapshot s, string group) => group switch
    {
        "CPU" => s.Names.Cpu,
        "GPU" => s.Names.Gpu,
        "Memory" => s.Names.Memory,
        "Storage" => s.Names.Storage,
        "Cooler" => s.Names.Cooler,
        _ => null,
    };

    /// <summary>
    /// One row per drive: the size (and drive letters) on the left with the free space underneath, and the
    /// temperature and health on the right. The full model name is the tooltip, so the rows stay one line each.
    /// </summary>
    private static List<RowData> StorageRows(SensorSnapshot s, HashSet<string> selected, TemperatureUnit unit)
    {
        if (!Metrics.All.Any(i => i.Group == "Storage" && selected.Contains(i.Key))) return [];
        if (s.Storage.Count == 0) return [new RowData("No drives reported", "—")];
        return s.Storage.Select(d =>
        {
            string label = d.TotalBytes is { } total && total > 0 ? Sizes.Capacity(total) : d.Name;
            if (d.Volumes is { Length: > 0 } letters) label += $" ({letters})";
            string temperature = LcdRenderer.FormatTemp(d.TemperatureCelsius, unit);
            string health = d.HealthPercent is { } h ? $"{h:0}% health{(h < 50 ? " ⚠" : "")}" : d.HealthState ?? "health —";
            string? detail = d.FreeBytes is { } free && d.TotalBytes is > 0 ? $"{Sizes.Capacity(free)} free" : null;
            return new RowData(label, $"{temperature} · {health}", detail, d.Name);
        }).ToList();
    }

    /// <summary>Drops the component name from a label inside its own card ("Memory usage" → "Usage").</summary>
    private static string ShortLabel(Metrics.Info info)
    {
        var text = info.Group switch
        {
            "Memory" => info.Label.Replace("Memory ", ""),
            "CPU" => info.Label.Replace("CPU ", ""),
            "GPU" => info.Label.Replace("GPU ", ""),
            "Cooler" => info.Label switch
            {
                "Coolant temperature" => "Coolant",
                "Pump speed" => "Pump",
                _ => info.Label.Replace("Radiator fan", "Fan"),
            },
            _ => info.Label,
        };
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : info.Label;
    }

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        var selected = _state.Preferences.DashboardMetrics.ToHashSet();
        var order = new[] { "CPU", "GPU", "Memory", "Storage", "Cooler" };
        _metricGroups = Metrics.All
            .GroupBy(m => m.Group)
            .OrderBy(g => Array.IndexOf(order, g.Key) is var i && i >= 0 ? i : order.Length)
            .Select(g => new MetricGroup
            {
                Name = g.Key,
                Items = g.Select(m => new MetricChoice
                {
                    Key = m.Key,
                    Label = string.IsNullOrEmpty(m.Unit) ? m.Label : $"{m.Label} ({m.Unit})",
                    Group = m.Group,
                    Selected = selected.Contains(m.Key),
                }).ToList(),
            })
            .ToList();
        foreach (var group in _metricGroups)
        {
            foreach (var item in group.Items)
                item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MetricChoice.Selected)) group.RefreshFromItems(); };
            group.RefreshFromItems();
        }
        MetricGroups.ItemsSource = _metricGroups;
        CustomizePopup.IsOpen = true;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _metricGroups) group.AllSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _metricGroups) group.AllSelected = false;
    }

    private void SaveMetrics_Click(object sender, RoutedEventArgs e)
    {
        var keys = _metricGroups.SelectMany(g => g.Items).Where(c => c.Selected).Select(c => c.Key).ToList();
        _state.SavePreferences(_state.Preferences with { DashboardMetrics = keys });
        CustomizePopup.IsOpen = false;
        Refresh();
    }
}
