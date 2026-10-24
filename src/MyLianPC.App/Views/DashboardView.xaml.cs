using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MyLianPC.App.Controls;
using MyLianPC.App.Services;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Network;
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

    /// <summary>One cell of the connection card: a label, its value and a quiet note underneath.</summary>
    public sealed partial class TileVm : ObservableObject
    {
        [ObservableProperty] private string _label = "";
        [ObservableProperty] private string _value = "—";
        [ObservableProperty] private string? _detail;
        [ObservableProperty] private string _tooltip = "";
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
        ["Connection"] = Color.FromRgb(0x4C, 0xC2, 0xFF),
    };

    private static readonly Color DownloadColor = Color.FromRgb(0x2D, 0x8C, 0xFF);
    private static readonly Color UploadColor = Color.FromRgb(0xE0, 0xA0, 0x00);

    /// <summary>Frozen brushes for the card accents and the legend dots, created once instead of per refresh.</summary>
    private static readonly IReadOnlyDictionary<string, SolidColorBrush> GroupBrushes =
        GroupColors.ToDictionary(kv => kv.Key, kv => Freeze(new SolidColorBrush(kv.Value)));

    /// <summary>The component cards, in the order they are read. The connection card spans the page separately.</summary>
    private static readonly string[] CardGroups = ["CPU", "GPU", "Memory", "Storage", "Cooler"];

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
    private readonly LegendVm _tempCpu, _tempGpu, _tempCoolant, _loadCpu, _loadGpu, _loadMemory, _netDown, _netUp;
    private readonly List<TileVm> _connectionTiles =
    [
        new() { Label = "Network" }, new() { Label = "Status" }, new() { Label = "Signal" }, new() { Label = "Link speed" },
        new() { Label = "IPv4" }, new() { Label = "IPv6" }, new() { Label = "Gateway" }, new() { Label = "DNS" },
    ];
    private readonly List<TileVm> _trafficTiles =
    [
        new() { Label = "Download now" }, new() { Label = "Upload now" },
        new() { Label = "Downloaded today" }, new() { Label = "Uploaded today" },
    ];

    public DashboardView()
    {
        InitializeComponent();
        DataContext = _state;
        _state.SensorsUpdated += (_, _) => Refresh();
        _state.PropertyChanged += State_PropertyChanged;
        Loaded += (_, _) => { RefreshProfiles(); Refresh(); };
        // The cards and legends are created once and then only their text changes, so the page is not rebuilt per sample.
        Cards.ItemsSource = _cardSource;
        ConnectionTiles.ItemsSource = _connectionTiles;
        TrafficTiles.ItemsSource = _trafficTiles;
        _tempCpu = new LegendVm { Brush = GroupBrushes["CPU"] };
        _tempGpu = new LegendVm { Brush = GroupBrushes["GPU"] };
        _tempCoolant = new LegendVm { Brush = GroupBrushes["Cooler"] };
        _loadCpu = new LegendVm { Brush = GroupBrushes["CPU"] };
        _loadGpu = new LegendVm { Brush = GroupBrushes["GPU"] };
        _loadMemory = new LegendVm { Brush = GroupBrushes["Memory"] };
        _netDown = new LegendVm { Brush = Freeze(new SolidColorBrush(DownloadColor)) };
        _netUp = new LegendVm { Brush = Freeze(new SolidColorBrush(UploadColor)) };
        TempLegend.ItemsSource = new[] { _tempCpu, _tempGpu, _tempCoolant };
        LoadLegend.ItemsSource = new[] { _loadCpu, _loadGpu, _loadMemory };
        NetworkLegend.ItemsSource = new[] { _netDown, _netUp };
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
        RefreshConnection();

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

        var n = _state.Network;
        NetworkChart.SetSeries(
        [
            new("Download", DownloadColor, _state.DownloadHistory),
            new("Upload", UploadColor, _state.UploadHistory),
        ]);
        _netDown.Text = $"Download {NetworkText.Rate(n.DownloadBytesPerSecond)}";
        _netUp.Text = $"Upload {NetworkText.Rate(n.UploadBytesPerSecond)}";
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
        foreach (var group in CardGroups)
        {
            var rows = group == "Storage"
                ? StorageRows(s, selected, unit)
                : Metrics.All
                    .Where(i => i.Group == group && selected.Contains(i.Key) && !ShownWithName(i))
                    .Select(i => new RowData(ShortLabel(i), LcdRenderer.Format(i.Key, s[i.Key], unit)))
                    .ToList();
            if (group == "Memory" && rows.Count > 0) AddAvailableMemory(rows, s);
            if (rows.Count == 0) continue;
            cards.Add((group, Spec(group), rows));
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

    private string? Spec(string group)
    {
        var names = _state.Sensors.Names;
        return group switch
        {
            "CPU" => names.Cpu,
            "GPU" => GpuSpec(names.Gpu),
            "Memory" => names.Memory,
            "Storage" => names.Storage,
            "Cooler" => names.Cooler,
            _ => null,
        };
    }

    /// <summary>
    /// The GPU name with its memory size ("NVIDIA GeForce RTX 5060 Ti · 16 GB"). The total is how the card is
    /// equipped, not a live reading, so it belongs on the name line rather than in a row of its own; it still
    /// follows the "VRAM total" choice in Customize.
    /// </summary>
    private string? GpuSpec(string? name)
    {
        if (!_state.Preferences.DashboardMetrics.Contains(Metrics.GpuVramTotal)) return name;
        if (_state.Sensors[Metrics.GpuVramTotal] is not { } total) return name;
        // Whole gigabytes: the reported total is the usable size (16303 MB), and nobody calls that card a 15.9 GB one.
        var size = $"{Math.Round(total):0} GB";
        return string.IsNullOrWhiteSpace(name) ? size : $"{name} · {size}";
    }

    /// <summary>Readings that are folded into their card's name line instead of getting a row.</summary>
    private static bool ShownWithName(Metrics.Info info) => info.Key == Metrics.GpuVramTotal;

    /// <summary>
    /// Fills the connection card: the link itself as a row of cells (network, status, signal, addresses) and the
    /// traffic counters underneath as four large numbers. Cells are fixed, so the grid stays even and a value that is
    /// not known reads "—" instead of leaving a hole. Hidden entirely when the service does not expose network
    /// information (an older service still running after an app update).
    /// </summary>
    private void RefreshConnection()
    {
        var n = _state.Network;
        ConnectionCard.Visibility = n.Available ? Visibility.Visible : Visibility.Collapsed;
        if (!n.Available) return;

        ConnectionSpec.Text = n.AdapterDescription ?? "";
        ConnectionSpec.Visibility = string.IsNullOrWhiteSpace(n.AdapterDescription) ? Visibility.Collapsed : Visibility.Visible;

        bool connected = n.Connected;
        string signal = "—";
        string? signalDetail = null;
        if (connected) signal = SignalText(n, out signalDetail);
        Set(_connectionTiles[0], n.IsWireless ? "Wi-Fi network" : "Network",
            connected ? (n.IsWireless ? n.NetworkName ?? "—" : "Ethernet") : "Disconnected", null, n.NetworkName);
        Set(_connectionTiles[1], "Status", connected ? n.Connectivity ?? "Connected" : "Disconnected", null, null);
        Set(_connectionTiles[2], "Signal", signal, signalDetail,
            connected && n.SignalPercent is null
                ? "Windows does not report the Wi-Fi signal quality to the background service (location access is off)."
                : null);
        Set(_connectionTiles[3], "Link speed", connected ? NetworkText.LinkSpeed(n.LinkSpeedBitsPerSecond) : "—", null, null);
        Address(_connectionTiles[4], "IPv4", n.IPv4Addresses);
        Address(_connectionTiles[5], "IPv6", n.IPv6Addresses);
        Set(_connectionTiles[6], "Gateway", n.Gateway ?? "—", null, null);
        Address(_connectionTiles[7], "DNS", n.DnsServers);

        Set(_trafficTiles[0], "Download now", NetworkText.Rate(n.DownloadBytesPerSecond), "live rate", null);
        Set(_trafficTiles[1], "Upload now", NetworkText.Rate(n.UploadBytesPerSecond), "live rate", null);
        Set(_trafficTiles[2], "Downloaded today", NetworkText.TodayBytes(n.DownloadBytesToday), "since midnight", null);
        Set(_trafficTiles[3], "Uploaded today", NetworkText.TodayBytes(n.UploadBytesToday), "since midnight", null);
    }

    /// <summary>"85%", with the band and channel underneath when the adapter reports them.</summary>
    private static string SignalText(NetworkSnapshot n, out string? detail)
    {
        if (n.SignalPercent is not { } signal)
        {
            // Windows only hands the signal out through the WLAN API, which it refuses without location access.
            detail = n.IsWireless ? "not shared by Windows" : null;
            return "—";
        }
        detail = n.Band is null ? null : n.Channel is { } channel ? $"{n.Band} · channel {channel}" : n.Band;
        return $"{signal:0}%";
    }

    /// <summary>An address cell: the first address, with the rest (and the full list) behind a note and the tooltip.</summary>
    private static void Address(TileVm tile, string label, IReadOnlyList<string> addresses)
    {
        Set(tile, label, addresses.Count == 0 ? "Not assigned" : addresses[0],
            addresses.Count > 1 ? $"+{addresses.Count - 1} more" : null,
            addresses.Count > 1 ? string.Join("\n", addresses) : null);
    }

    private static void Set(TileVm tile, string label, string value, string? detail, string? tooltip)
    {
        tile.Label = label;
        tile.Value = value;
        tile.Detail = detail;
        tile.Tooltip = string.IsNullOrWhiteSpace(tooltip) ? value : tooltip;
    }

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

    /// <summary>
    /// Adds the memory still free under "Used". The service reports used and total, not free, but free is the number
    /// people look for first. Kept next to "Used" whatever metric order the account has chosen.
    /// </summary>
    private static void AddAvailableMemory(List<RowData> rows, SensorSnapshot s)
    {
        if (s[Metrics.RamUsedGb] is not { } used || s[Metrics.RamTotalGb] is not { } total || total <= 0) return;
        double free = Math.Max(0, total - used);
        var row = new RowData("Available", $"{free:0.#} GB", $"{total:0.#} GB total");
        int at = rows.FindIndex(r => r.Label == "Used");
        rows.Insert(at >= 0 ? at + 1 : 0, row);
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
                    Label = m.Key == Metrics.GpuVramTotal
                        ? "VRAM total (with the GPU name)"
                        : string.IsNullOrEmpty(m.Unit) ? m.Label : $"{m.Label} ({m.Unit})",
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
