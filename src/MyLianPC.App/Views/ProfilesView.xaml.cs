using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MyLianPC.App.Services;
using MyLianPC.Core.Profiles;

namespace MyLianPC.App.Views;

public partial class ProfilesView : UserControl
{
    public sealed record Item(HardwareProfile Profile, bool IsActive)
    {
        public string Summary => string.Join(" · ", new[]
        {
            Profile.Lighting is { SyncAll: { } s } ? $"{s.Effect} lighting" : Profile.Lighting is null ? null : "per-zone lighting",
            Profile.Lcd is { } l ? (l.Mode == LcdMode.Theme ? $"LCD: {l.ThemeId}" : $"LCD: {l.Mode}") : null,
            Profile.Cooling is { } c ? $"{c.Curves.Count} curve(s)" : "default cooling",
        }.Where(x => x is not null));
    }

    public sealed record ResultLine(string Icon, string Part, string Message);

    private readonly AppState _state = AppState.Current;

    public ProfilesView()
    {
        InitializeComponent();
        _state.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppState.Profiles) or nameof(AppState.Status)) Refresh(); };
        Loaded += (_, _) => Refresh();
    }

    private HardwareProfile? Selected => (List.SelectedItem as Item)?.Profile;

    private void Refresh()
    {
        var keep = Selected?.Id;
        var items = _state.Profiles.Profiles.OrderBy(p => p.Name).Select(p => new Item(p, p.Id == _state.Profiles.ActiveProfileId)).ToList();
        List.ItemsSource = items;
        List.SelectedItem = items.FirstOrDefault(i => i.Profile.Id == keep) ?? items.FirstOrDefault(i => i.IsActive) ?? items.FirstOrDefault();
        LastApply.ItemsSource = _state.Status?.LastApply?.Results.Select(r => new ResultLine(r.Success ? "✔" : "✖", r.Part, r.Message)).ToList();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } p) return;
        DetailTitle.Text = p.Name + (p.Id == _state.Profiles.ActiveProfileId ? "  (active)" : "");
        var lines = new List<string>();
        if (p.Lighting?.SyncAll is { } s) lines.Add($"Lighting: {s.Effect} on all devices, {string.Join(" ", s.Colors.Select(c => c.ToHex()))}, brightness {s.Brightness}%");
        else if (p.Lighting is { } l) lines.Add($"Lighting: per zone ({l.Zones.Count} zones)");
        else lines.Add("Lighting: not changed by this profile");
        lines.Add(p.Lcd is { } lcd ? $"LCD: {lcd.Mode} {(lcd.Mode == LcdMode.Theme ? lcd.ThemeId : lcd.MediaFile)}, brightness {lcd.Brightness}%" : "LCD: default theme");
        lines.Add(p.Cooling is { } c ? "Cooling: " + string.Join(", ", c.Curves.Select(kv => $"{kv.Key} follows {kv.Value.Source}")) : "Cooling: default Balanced curves");
        lines.Add($"Last changed {p.ModifiedUtc.ToLocalTime():g}");
        DetailText.Text = string.Join("\n", lines);
    }

    private async Task AfterChange()
    {
        await _state.RefreshProfilesAsync();
        await _state.RefreshStatusAsync();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var r = await AppState.TryAsync(c => c.ActivateAsync(p.Id), "Activating the profile");
        await AfterChange();
        if (r is not null) ApplyReportDialog.Show(r);
    }

    private async void Reapply_Click(object sender, RoutedEventArgs e)
    {
        var r = await AppState.TryAsync(c => c.ReapplyAsync(), "Re-applying the profile");
        await AfterChange();
        if (r is not null) ApplyReportDialog.Show(r);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (TextPrompt.Ask(Window.GetWindow(this)!, "New profile", "Name of the new profile (it starts as a copy of the active profile):", "My profile") is not { } name) return;
        var baseProfile = _state.ActiveProfile;
        var p = baseProfile is null
            ? new HardwareProfile { Id = Guid.NewGuid(), Name = name }
            : baseProfile with { Id = Guid.NewGuid(), Name = name };
        await AppState.TryAsync(c => c.SaveProfileAsync(p), "Creating the profile");
        await AfterChange();
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (TextPrompt.Ask(Window.GetWindow(this)!, "Duplicate profile", "Name of the copy:", p.Name + " copy") is not { } name) return;
        await AppState.TryAsync(c => c.DuplicateAsync(p.Id, name), "Duplicating the profile");
        await AfterChange();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (TextPrompt.Ask(Window.GetWindow(this)!, "Rename profile", "New name:", p.Name) is not { } name) return;
        await AppState.TryAsync(c => c.RenameAsync(p.Id, name), "Renaming the profile");
        await AfterChange();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete '{p.Name}' for every Windows account on this PC?", "Delete profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await AppState.TryAsync(c => c.DeleteProfileAsync(p.Id), "Deleting the profile");
        await AfterChange();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export profile",
            FileName = SafeFileName(p.Name) + ".mylianpc.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "MyLianPC profile|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var json = await AppState.TryAsync(c => c.ExportAsync(p.Id), "Exporting the profile");
        if (json is null) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"Could not write the file:\n\n{ex.Message}", "Export profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import profile",
            DefaultExt = ".json",
            Filter = "MyLianPC profile|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string json;
        try
        {
            json = await File.ReadAllTextAsync(dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"Could not read the file:\n\n{ex.Message}", "Import profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var imported = await AppState.TryAsync(c => c.ImportAsync(json), "Importing the profile");
        if (imported is null) return;
        await AfterChange();
        // Show what arrived: the copy has a fresh id, so select it by id instead of leaving the active profile selected.
        if (List.ItemsSource is IEnumerable<Item> items && items.FirstOrDefault(i => i.Profile.Id == imported.Id) is { } added)
            List.SelectedItem = added;
    }

    /// <summary>Strips the characters Windows does not allow in a file name ("Custom: RGB" would fail to save).</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }
}
