using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MyLianPC.App.Services;

namespace MyLianPC.App.Views;

/// <summary>Everything about the build in one place: version, where it runs from, the service it talks to, the
/// hardware it was made for and where its data lives.</summary>
public partial class AboutView : UserControl
{
    private const string Repository = "https://github.com/abdh01h/MyLianPC";
    private readonly AppState _state = AppState.Current;

    public AboutView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        _state.PropertyChanged += (_, e) =>
        {
            if (!IsLoaded) return;
            if (e.PropertyName is nameof(AppState.Status) or nameof(AppState.Sensors)) Refresh();
        };
        _state.SensorsUpdated += (_, _) => { if (IsLoaded) RefreshHardware(); };
    }

    private void Refresh()
    {
        string version = InformationalVersion();
        VersionBadge.Text = version;
        AppVersion.Text = version;
        BuildDate.Text = BuiltOn()?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        RuntimeText.Text = RuntimeInformation.FrameworkDescription;
        InstallFolder.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DataFolder.Text = SharedFolder;
        LogFolder.Text = Path.Combine(SharedFolder, "logs");
        ServiceValue.Text = _state.Status is { } status
            ? $"Running · version {status.Version} · since {status.StartedUtc.ToLocalTime():g}"
            : SystemIntegration.ServiceState();
        WindowsText.Text = RuntimeInformation.OSDescription;
        RefreshHardware();
    }

    private void RefreshHardware()
    {
        var names = _state.Sensors.Names;
        CpuName.Text = Fallback(names.Cpu);
        GpuName.Text = Fallback(names.Gpu);
        MemoryName.Text = Fallback(names.Memory);
        StorageName.Text = Fallback(names.Storage);
        CoolerName.Text = Fallback(names.Cooler);
    }

    private static string Fallback(string? text) => string.IsNullOrWhiteSpace(text) ? "—" : text;

    private static string SharedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyLianPC");

    private static string InformationalVersion()
    {
        var assembly = typeof(AboutView).Assembly;
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+'); // source-revision suffix from the SDK
            return plus >= 0 ? info[..plus] : info;
        }
        return assembly.GetName().Version?.ToString(3) ?? "—";
    }

    private static DateTime? BuiltOn()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (path is not null && File.Exists(path)) return File.GetLastWriteTime(path);
        }
        catch (IOException) { }
        return null;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }

    private void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Open(path);
        else MessageBox.Show(Window.GetWindow(this)!, $"{path}\n\nThis folder does not exist yet.", "MyLianPC",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Install_Click(object sender, RoutedEventArgs e) => OpenFolder(AppContext.BaseDirectory);
    private void Data_Click(object sender, RoutedEventArgs e) => OpenFolder(SharedFolder);
    private void Logs_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(SharedFolder, "logs"));
    private void GitHub_Click(object sender, RoutedEventArgs e) => Open(Repository);
    private void Releases_Click(object sender, RoutedEventArgs e) => Open(Repository + "/releases");
    private void Support_Click(object sender, RoutedEventArgs e) => Open(Core.Drivers.GigabyteSupport.SupportUrl);

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (!File.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, $"{path}\n\nThe notices file is missing from this install.",
                "MyLianPC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Windows has no default program for .md on most PCs, and the shell then opens nothing at all, so a
        // failure falls back to Notepad rather than leaving the button looking dead.
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false }); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this)!, $"Could not open {path}:\n\n{ex.Message}", "MyLianPC",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
