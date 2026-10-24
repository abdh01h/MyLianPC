using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyLianPC.App.Services;
using MyLianPC.Core.Drivers;

namespace MyLianPC.App.Views;

public partial class DriversView : UserControl
{
    /// <summary>One package from the support page, matched against what this PC has installed.</summary>
    public sealed record DriverRow(string Name, string Category, string Installed, string Latest, string State, Brush StateBrush, string Url, string Hint);

    private BoardInfo? _board;
    private IReadOnlyList<InstalledItem> _installed = [];
    private SupportCatalog? _catalog;
    private bool _loaded, _busy;

    public DriversView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try { _board = await DriversService.ReadBoardAsync(); }
        catch (Exception e) when (e is InvalidOperationException or System.Management.ManagementException) { }
        try { _installed = await DriversService.ReadInstalledAsync(); }
        catch (Exception e) when (e is InvalidOperationException or System.Management.ManagementException) { }
        RenderBoard();

        var cache = DriversService.LoadCache();
        if (cache is not null)
        {
            Render(cache);
            LastChecked.Text = $"Last checked {cache.FetchedUtc.ToLocalTime():g}";
        }

        // A cached list older than a day is refreshed in the background of opening this page, once. With nothing
        // cached the first visit fetches it, which is the only way the page can be useful.
        if (cache is null || cache.FetchedUtc < DateTimeOffset.UtcNow.AddDays(-1)) await CheckAsync();
    }

    private void RenderBoard()
    {
        if (_board is null) return;
        BoardValue.Text = string.IsNullOrWhiteSpace(_board.Name) ? "—" : _board.Name;
        BiosValue.Text = string.IsNullOrWhiteSpace(_board.BiosVersion)
            ? "—"
            : _board.BiosDate is { } date ? $"{_board.BiosVersion} (released {date:yyyy-MM-dd})" : _board.BiosVersion;
    }

    private void Render(SupportCatalog catalog)
    {
        _catalog = catalog;

        var latest = catalog.Bios.FirstOrDefault();
        if (latest is null)
        {
            LatestBios.Text = "Not listed";
            BiosBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            LatestBios.Text = string.IsNullOrWhiteSpace(latest.Date) ? latest.Version : $"{latest.Version} ({latest.Date})";
            int? comparison = string.IsNullOrWhiteSpace(_board?.BiosVersion) ? null : Versions.CompareBios(_board!.BiosVersion, latest.Version);
            SetBiosStatus(comparison);
            BiosDownload.Tag = latest.DownloadUrl;
            BiosDownload.IsEnabled = true;
            if (latest.Notes.Count > 0)
            {
                BiosNotes.Text = "What's new: " + string.Join("  ·  ", latest.Notes.Take(4)) + (latest.Notes.Count > 4 ? " …" : "");
                BiosNotes.Visibility = Visibility.Visible;
            }
        }

        var rows = catalog.Drivers.Select(entry =>
        {
            var status = DriverMatcher.Evaluate(entry, _installed);
            return new DriverRow(entry.Name, entry.Category, status.Installed?.Version ?? "—", entry.Version,
                status.Label, StateBrush(status.State), entry.DownloadUrl, Hint(status.State));
        }).ToList();

        DriverList.ItemsSource = rows;
        EmptyDrivers.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBiosStatus(int? comparison)
    {
        var (label, brush) = comparison switch
        {
            < 0 => ("Update available", Resource("SystemFillColorCautionBackgroundBrush")),
            0 => ("Up to date", Resource("SystemFillColorSuccessBackgroundBrush")),
            > 0 => ("Newer than the site", Resource("SystemFillColorSuccessBackgroundBrush")),
            null => ("Check manually", Resource("ControlFillColorSecondaryBrush")),
        };
        BiosBadgeText.Text = label;
        BiosBadge.Background = brush;
        BiosBadge.Visibility = Visibility.Visible;
    }

    /// <summary>What a status badge means, shown as its tooltip. "Check manually" in particular needs saying why.</summary>
    private static string Hint(DriverState state) => state switch
    {
        DriverState.UpdateAvailable => "GIGABYTE lists a newer version than the one this PC has.",
        DriverState.UpToDate => "The installed version matches the latest one GIGABYTE lists.",
        DriverState.NewerInstalled => "This PC already has a newer version than the one GIGABYTE lists.",
        DriverState.NotDetected => "No matching driver or program was found on this PC, or Windows reports no version for it. Install it only if the device needs it.",
        DriverState.SetupOnly => "An installer that is used during Windows Setup; it has no version to compare and is not normally installed.",
        DriverState.Manual => "The installed driver and GIGABYTE's package number their versions differently (AMD lists the Adrenalin package version, Windows reports the graphics driver version), so MyLianPC cannot tell which is newer. Compare the two versions, or use Download for GIGABYTE's package.",
        _ => "The installed version could not be read, so there is nothing to compare.",
    };

    private static Brush StateBrush(DriverState state) => state switch
    {
        DriverState.UpdateAvailable => Resource("SystemFillColorCautionBackgroundBrush"),
        DriverState.UpToDate or DriverState.NewerInstalled => Resource("SystemFillColorSuccessBackgroundBrush"),
        DriverState.NotDetected => Resource("SystemFillColorCriticalBackgroundBrush"),
        _ => Resource("ControlFillColorSecondaryBrush"),
    };

    /// <summary>Theme brushes; the fallback keeps the badges readable if a Windows build lacks one.</summary>
    private static Brush Resource(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.DimGray;

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        CheckButton.IsEnabled = false;
        CheckButton.Content = "Checking…";
        try
        {
            var catalog = await DriversService.RefreshAsync();
            HideError();
            Render(catalog);
            LastChecked.Text = $"Checked {catalog.FetchedUtc.ToLocalTime():g}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException
                                    or InvalidDataException or IOException or JsonException)
        {
            string message = $"Could not read GIGABYTE's support page: {e.Message}";
            if (_catalog is null) ShowError(message + "\n\nOpen the support page in a browser to check manually.");
            else LastChecked.Text = "Refresh failed · showing the cached list";
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
            CheckButton.Content = "Check for updates";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void Support_Click(object sender, RoutedEventArgs e) => DriversService.Open(GigabyteSupport.SupportUrl);

    private void DownloadBios_Click(object sender, RoutedEventArgs e)
    {
        if (BiosDownload.Tag is string url && url.Length > 0) DriversService.Open(url);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && url.Length > 0) DriversService.Open(url);
    }
}
