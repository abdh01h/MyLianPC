using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MyLianPC.App.Services;
using MyLianPC.Core.Devices;

namespace MyLianPC.App.Views;

public partial class DevicesView : UserControl
{
    public sealed partial class ZoneVm : ObservableObject
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required bool Configurable { get; init; }
        public required string Effects { get; init; }
        [ObservableProperty] private int _ledCount;
    }

    public sealed partial class FanVm : ObservableObject
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required bool CanOptIn { get; init; }
        public string? Note { get; init; }
        [ObservableProperty] private string _rpmText = "";
        [ObservableProperty] private bool _controlEnabled;
    }

    public sealed class DeviceVm
    {
        public required DeviceStatus Status { get; init; }
        public required List<ZoneVm> Zones { get; init; }
        public required List<FanVm> Fans { get; init; }
        public bool HasZones => Zones.Count > 0;
        public bool HasFans => Fans.Count > 0;
        public string Subtitle => string.Join("  ·  ", new[]
        {
            Status.Kind,
            Status.Firmware is { } f ? $"firmware {f}" : null,
            Status.Location,
            Status.LastContactUtc is { } t ? $"last contact {t.ToLocalTime():HH:mm:ss}" : null,
        }.Where(x => x is not null));
    }

    private readonly AppState _state = AppState.Current;
    private List<DeviceVm> _vms = [];

    public DevicesView()
    {
        InitializeComponent();
        _state.PropertyChanged += State_PropertyChanged;
        Loaded += (_, _) => Build();
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.Status) || _state.Status is not { } st) return;
        bool sameShape = _vms.Select(v => (v.Status.Id, v.Status.State, v.Zones.Count, v.Fans.Count, v.Status.Error))
            .SequenceEqual(st.Devices.Select(d => (d.Id, d.State, d.Zones.Count, d.Fans.Count, d.Error)));
        if (!sameShape) { Build(); return; }
        // Same devices: only refresh live readings so edits in progress are kept.
        foreach (var d in st.Devices)
            foreach (var f in d.Fans)
                if (_vms.SelectMany(v => v.Fans).FirstOrDefault(x => x.Id == f.Id) is { } vm) vm.RpmText = Rpm(f);
    }

    private static string Rpm(FanChannelInfo f) => f.Rpm is { } r ? $"{r:0} rpm" : "n/a";

    private void Build()
    {
        if (_state.Status is not { } st) return;
        _vms = st.Devices.Select(d => new DeviceVm
        {
            Status = d,
            Zones = d.Zones.Select(z => new ZoneVm
            {
                Id = z.Id, Name = z.Name, Configurable = z.LedCountConfigurable, LedCount = z.LedCount,
                Effects = string.Join(", ", z.Effects),
            }).ToList(),
            Fans = d.Fans.Select(f => new FanVm
            {
                Id = f.Id, Name = $"{f.Name} ({f.Controller})", CanOptIn = f.Controllable && f.Id.StartsWith("mb:"),
                Note = f.Note, RpmText = Rpm(f), ControlEnabled = f.ControlEnabled,
            }).ToList(),
        }).ToList();
        DeviceList.ItemsSource = _vms;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        await AppState.TryAsync(c => c.RescanAsync(), "Rescanning devices");
        await _state.RefreshStatusAsync();
        Build();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Status is not { } st) return;
        var optIn = _vms.SelectMany(v => v.Fans).Where(f => f.CanOptIn && f.ControlEnabled).Select(f => f.Id).ToList();
        var newlyEnabled = optIn.Except(st.DeviceSettings.ControlledMotherboardFans).ToList();
        if (newlyEnabled.Count > 0 && MessageBox.Show(Window.GetWindow(this)!,
                "MyLianPC will drive these motherboard fan headers in software instead of the BIOS Smart Fan curve:\n\n" +
                string.Join("\n", newlyEnabled) +
                "\n\nThey follow the curves you set on the Cooling page (100% if a sensor fails) and go back to BIOS control when the service stops. Continue?",
                "Motherboard fan control", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var settings = st.DeviceSettings with
        {
            ZoneLedCounts = _vms.SelectMany(v => v.Zones).Where(z => z.Configurable).ToDictionary(z => z.Id, z => Math.Clamp(z.LedCount, 1, 256)),
            ControlledMotherboardFans = optIn,
        };
        await AppState.TryAsync(c => c.ConfigureDevicesAsync(settings), "Saving device settings");
        await _state.RefreshStatusAsync();
        Build();
    }
}
