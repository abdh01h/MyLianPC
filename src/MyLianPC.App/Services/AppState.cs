using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MyLianPC.Core.Ipc;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;

namespace MyLianPC.App.Services;

/// <summary>
/// Rolling history of one metric for the charts. A fixed-size ring buffer, so a tick adds a value without
/// allocating (the charts read it directly; nothing is copied per sample).
/// </summary>
public sealed class MetricHistory(int capacity) : IReadOnlyList<double?>
{
    private readonly double?[] _values = new double?[Math.Max(1, capacity)];
    private int _next;

    public int Count { get; private set; }
    public int Capacity => _values.Length;

    /// <summary>0 is the oldest sample, <see cref="Count"/> - 1 the newest.</summary>
    public double? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int start = _next - Count;
            if (start < 0) start += _values.Length;
            return _values[(start + index) % _values.Length];
        }
    }

    public void Add(double? value)
    {
        _values[_next] = value;
        _next = (_next + 1) % _values.Length;
        if (Count < _values.Length) Count++;
    }

    public IEnumerator<double?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Shared state for every page: the service connection, latest status/sensors/profiles and this Windows
/// user's preferences. Polls the service on the UI thread's dispatcher; all I/O is async so the UI stays responsive.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    public static AppState Current { get; } = new();

    private readonly DispatcherTimer _sensorTimer = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _sensorBusy, _statusBusy, _suspended;

    public ServiceClient Client { get; } = new();
    public Dictionary<string, MetricHistory> History { get; } = [];

    [ObservableProperty] private ServiceStatus? _status;
    [ObservableProperty] private SensorSnapshot _sensors = new();
    [ObservableProperty] private ProfileDocument _profiles = new();
    [ObservableProperty] private UserPreferences _preferences = UserPreferences.Load(UserPreferences.DefaultPath).Normalized();
    [ObservableProperty] private bool _serviceConnected;
    [ObservableProperty] private string _connectionMessage = "Connecting to the MyLianPC service…";

    public event EventHandler? SensorsUpdated;

    public HardwareProfile? ActiveProfile => Profiles.Profiles.FirstOrDefault(p => p.Id == Profiles.ActiveProfileId);

    private AppState()
    {
        _sensorTimer.Tick += async (_, _) => await RefreshSensorsAsync();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    public async Task StartAsync()
    {
        _sensorTimer.Interval = TimeSpan.FromMilliseconds(Preferences.DashboardRefreshMilliseconds);
        _sensorTimer.Start();
        _statusTimer.Start();
        await RefreshStatusAsync();
        await RefreshProfilesAsync();
        await RefreshSensorsAsync();
    }

    /// <summary>True while the window is hidden or minimized, so no readings are being polled.</summary>
    public bool PollingSuspended => _suspended;

    /// <summary>
    /// Window hidden (tray) or minimized: stop reading the sensors at the dashboard rate. The service keeps
    /// controlling the hardware on its own; only the periodic reads stop. Status is still checked every 30 s so the
    /// tray menu and the connection state stay correct.
    /// </summary>
    public void SuspendPolling()
    {
        _suspended = true;
        _sensorTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(30);
    }

    /// <summary>The window came back: resume the dashboard rate and refresh right away so nothing looks stale.</summary>
    public void ResumePolling()
    {
        if (!_suspended) return;
        _suspended = false;
        _statusTimer.Interval = TimeSpan.FromSeconds(3);
        _sensorTimer.Start();
        _statusTimer.Start();
        _ = RefreshStatusAsync();
        _ = RefreshSensorsAsync();
    }

    public void SavePreferences(UserPreferences prefs)
    {
        Preferences = prefs.Normalized();
        Preferences.Save(UserPreferences.DefaultPath);
        _sensorTimer.Interval = TimeSpan.FromMilliseconds(Preferences.DashboardRefreshMilliseconds);
    }

    public async Task RefreshStatusAsync()
    {
        if (_statusBusy) return;
        _statusBusy = true;
        try
        {
            Status = await Client.StatusAsync();
            if (Status.ActiveProfileId != Profiles.ActiveProfileId) await RefreshProfilesAsync();
            SetConnected(true, null);
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or ServiceCallException) { SetConnected(false, e.Message); }
        finally { _statusBusy = false; }
    }

    public async Task RefreshProfilesAsync()
    {
        try
        {
            Profiles = await Client.ProfilesAsync();
            OnPropertyChanged(nameof(ActiveProfile));
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or ServiceCallException) { SetConnected(false, e.Message); }
    }

    private async Task RefreshSensorsAsync()
    {
        if (_sensorBusy || _suspended || !ServiceConnected) return;
        _sensorBusy = true;
        try
        {
            Sensors = await Client.SensorsAsync();
            foreach (var m in Metrics.All)
            {
                if (!History.TryGetValue(m.Key, out var h)) History[m.Key] = h = new MetricHistory(300);
                h.Add(Sensors[m.Key]);
            }
            SensorsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or ServiceCallException) { SetConnected(false, e.Message); }
        finally { _sensorBusy = false; }
    }

    private void SetConnected(bool ok, string? message)
    {
        ServiceConnected = ok;
        ConnectionMessage = ok ? "Connected" : message ?? "Service unavailable";
    }

    /// <summary>Runs a service call, shows failures to the user and returns default on error.</summary>
    public static async Task<T?> TryAsync<T>(Func<ServiceClient, Task<T>> call, string what)
    {
        try { return await call(Current.Client); }
        catch (Exception e) when (e is ServiceUnavailableException or ServiceCallException or IOException)
        {
            MessageBox.Show(Application.Current.MainWindow!, $"{what} failed:\n\n{e.Message}", "MyLianPC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return default;
        }
    }
}
