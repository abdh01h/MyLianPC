using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MyLianPC.App.Services;
using MyLianPC.Core.Settings;

namespace MyLianPC.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppState _state = AppState.Current;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { LoadPrefs(); await RefreshVendorAsync(); };
        // Ctrl + / Ctrl − change the size from anywhere in the window; keep this slider in step with it.
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppState.Preferences) || !IsLoaded) return;
            double wanted = _state.Preferences.UiScale * 100;
            if (Math.Abs(ScaleSlider.Value - wanted) < 0.1) return;
            _loading = true;
            ScaleSlider.Value = wanted;
            ScaleValue.Text = $"{wanted:0}%";
            _loading = false;
        };
    }

    private void LoadPrefs()
    {
        _loading = true;
        var p = _state.Preferences;
        (p.Appearance switch { AppearanceMode.Light => ThemeLight, AppearanceMode.System => ThemeSystem, _ => ThemeDark }).IsChecked = true;
        (p.TemperatureUnit == TemperatureUnit.Fahrenheit ? UnitF : UnitC).IsChecked = true;
        RefreshSlider.Value = p.DashboardRefreshMilliseconds;
        ScaleSlider.Value = p.UiScale * 100;
        ScaleValue.Text = $"{p.UiScale * 100:0}%";
        TrayBox.IsChecked = p.MinimizeToTray;
        ShowStartupState();
        FlashBox.IsChecked = _state.Status?.DeviceSettings.SaveMotherboardLightingToFlash ?? true;
        FanLightingBox.IsChecked = _state.Status?.DeviceSettings.CoolerFanLighting ?? false;
        ServiceState.Text = $"MyLianPC service: {SystemIntegration.ServiceState()}" + (_state.Status is { } st ? $" · version {st.Version} · running since {st.StartedUtc.ToLocalTime():g}" : "");
        DynamicState.Text = SystemIntegration.DynamicLightingEnabledForMotherboard switch
        {
            true => "On: Windows may repaint the motherboard/case lighting in this account. Turn it off so MyLianPC's colors stay.",
            false => "Off for the motherboard in this account. ✔",
            null => "Windows has not registered the motherboard as a Dynamic Lighting device in this account.",
        };
        About.Text = $"MyLianPC {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)} — free and open source (GPL-2.0-or-later).\n" +
                     "Unified lighting, cooler LCD, cooling and monitoring for this PC. Not affiliated with Lian Li, GIGABYTE or Corsair.";
        _loading = false;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = ThemeLight.IsChecked == true ? AppearanceMode.Light : ThemeSystem.IsChecked == true ? AppearanceMode.System : AppearanceMode.Dark;
        _state.SavePreferences(_state.Preferences with { Appearance = mode });
        SystemIntegration.ApplyTheme(mode); // UI only: no service call, hardware is untouched
    }

    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleValue is null) return;
        ScaleValue.Text = $"{ScaleSlider.Value:0}%";
        if (_loading || !IsLoaded) return;
        if (Window.GetWindow(this) is MainWindow main) main.ApplyZoom(ScaleSlider.Value / 100.0, showToast: false);
    }

    private void Pref_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var unit = UnitF.IsChecked == true ? TemperatureUnit.Fahrenheit : TemperatureUnit.Celsius;
        bool unitChanged = unit != _state.Preferences.TemperatureUnit;
        _state.SavePreferences(_state.Preferences with
        {
            TemperatureUnit = unit,
            DashboardRefreshMilliseconds = (int)RefreshSlider.Value,
            MinimizeToTray = TrayBox.IsChecked == true,
        });
        if (unitChanged) _ = SyncLcdUnitAsync(unit);
    }

    /// <summary>
    /// The cooler LCD is drawn by the service from the active profile, so the profile's own unit is kept in step with
    /// the reading unit chosen here — otherwise the panel would keep showing the other scale.
    /// </summary>
    private async Task SyncLcdUnitAsync(TemperatureUnit unit)
    {
        try
        {
            var doc = await _state.Client.ProfilesAsync();
            if (doc.Profiles.FirstOrDefault(p => p.Id == doc.ActiveProfileId) is not { Lcd: { } lcd } active || lcd.Unit == unit) return;
            await _state.Client.SaveProfileAsync(active with { Lcd = lcd with { Unit = unit } });
        }
        catch (Exception) { /* service unavailable: the LCD keeps the unit stored in its profile */ }
    }

    private async void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        bool wanted = StartupBox.IsChecked == true;
        StartupBox.IsEnabled = false;
        bool applied = await SystemIntegration.SetLaunchAtLoginAsync(wanted);
        StartupBox.IsEnabled = true;
        if (!applied)
            MessageBox.Show(Window.GetWindow(this)!,
                "The start-up entry is machine-wide (written by the installer) and was not removed because administrator approval was declined.",
                "MyLianPC");
        _state.SavePreferences(_state.Preferences with { LaunchAtLogin = applied ? wanted : SystemIntegration.LaunchAtLogin });
        ShowStartupState();
    }

    private void ShowStartupState()
    {
        StartupBox.IsChecked = SystemIntegration.LaunchAtLogin;
        StartupState.Text = SystemIntegration.StartupLocation switch
        {
            "this account" => $"Enabled for {Environment.UserName}; starts in the notification area after sign-in.",
            "all accounts" => "Enabled for all Windows accounts by the installer (needs administrator approval to turn off).",
            _ => "Not enabled: MyLianPC opens only when you start it. Lighting, the LCD and cooling are still controlled by the service, which always starts with Windows.",
        };
    }

    private async void Flash_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Status is not { } st) return;
        await AppState.TryAsync(c => c.ConfigureDevicesAsync(st.DeviceSettings with { SaveMotherboardLightingToFlash = FlashBox.IsChecked == true }), "Saving the setting");
        await _state.RefreshStatusAsync();
    }

    private async void FanLighting_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Status is not { } st) return;
        await AppState.TryAsync(c => c.ConfigureDevicesAsync(st.DeviceSettings with { CoolerFanLighting = FanLightingBox.IsChecked == true }), "Saving the setting");
        await _state.RefreshStatusAsync();
        FanLightingBox.IsChecked = _state.Status?.DeviceSettings.CoolerFanLighting ?? false; // the service may have turned it back off
    }

    private async void RestartService_Click(object sender, RoutedEventArgs e)
    {
        if (!await SystemIntegration.RestartServiceAsync())
            MessageBox.Show(Window.GetWindow(this)!, "The service was not restarted (administrator approval was declined or the service is not installed).", "MyLianPC");
        await Task.Delay(1500);
        await _state.RefreshStatusAsync();
        LoadPrefs();
    }

    private void Logs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyLianPC", "logs")) { UseShellExecute = true });

    private async Task RefreshVendorAsync()
    {
        try { VendorList.ItemsSource = (await _state.Client.VendorStatusAsync()).Components.Where(c => c.Present).ToList(); }
        catch (Exception) { VendorList.ItemsSource = null; }
    }

    private async void Takeover_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!,
                "This will:\n\n• stop L-Connect 3's services and keep them from starting\n• disable Corsair Device Control Service\n• turn off GIGABYTE Control Center's sign-in task and close it\n\n" +
                "Nothing is uninstalled. You can undo this with 'Restore vendor software'. Continue?",
                "Take over from vendor software", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!await SystemIntegration.VendorTakeoverAsync())
            MessageBox.Show(Window.GetWindow(this)!, "Administrator approval was declined, so nothing was changed.", "MyLianPC");
        await AppState.TryAsync(c => c.RescanAsync(), "Rescanning devices");
        await RefreshVendorAsync();
        await _state.RefreshStatusAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "Re-enable L-Connect 3, Corsair Device Control Service and GIGABYTE Control Center autostart as they were before?\n\nThey will compete with MyLianPC for the same hardware again.",
                "Restore vendor software", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!await SystemIntegration.VendorRestoreAsync())
            MessageBox.Show(Window.GetWindow(this)!, "Administrator approval was declined, so nothing was changed.", "MyLianPC");
        await RefreshVendorAsync();
    }

    private void Dynamic_Click(object sender, RoutedEventArgs e)
    {
        SystemIntegration.DisableDynamicLightingForMotherboard();
        _state.SavePreferences(_state.Preferences with { DynamicLightingPromptShown = true });
        LoadPrefs();
    }

    private async void ResetProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "Replace all profiles (for every account) with the example profiles?", "Reset profiles",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await AppState.TryAsync(c => c.ResetProfilesAsync(), "Resetting profiles");
        await _state.RefreshProfilesAsync();
    }

    private async void RestoreCooling_Click(object sender, RoutedEventArgs e)
    {
        await AppState.TryAsync(c => c.RestoreCoolingAsync(), "Restoring default cooling");
        await _state.RefreshStatusAsync();
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\""));
    }
}
